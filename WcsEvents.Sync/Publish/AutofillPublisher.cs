using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Cloud.Firestore;
using Microsoft.EntityFrameworkCore;
using WcsEvents.Sync.Data;

namespace WcsEvents.Sync.Publish;

/// <summary>
/// Copies event details read off organisers' websites (<c>data/autofill.json</c>, kept in the repo)
/// into the admin's <c>info/{id}</c> documents. Only fields the admin has left empty are filled, and
/// each entry is applied once: an admin who clears a field afterwards does not see it come back
/// until the entry itself changes. Filled documents carry an <c>autofill</c> note the pages show
/// until the admin saves the event. While that note is still there nobody has edited the document,
/// so a changed entry corrects every field it names.
/// </summary>
public sealed partial class AutofillPublisher(FirestoreDb firestore, FirestoreStore store, AppDbContext db, TimeProvider clock, ILogger<AutofillPublisher> logger)
{
    /// <summary>Part of every entry's hash: change it when the rules for applying entries change, and each entry is applied again.</summary>
    private const string Rules = "|corrects-untouched-v1";

    public async Task PublishAsync(string file, CancellationToken ct)
    {
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(file, ct));
        var existing = await store.ReadAllAsync("info", ct);
        var years = await db.ScoringEvents.AsNoTracking()
            .Where(e => e.DateFrom != null)
            .ToDictionaryAsync(e => e.Id.ToString(), e => e.DateFrom!.Value.Year, ct);

        var applied = 0;
        foreach (var entry in json.RootElement.EnumerateObject())
        {
            var key = $"{firestore.ProjectId}/autofill/{entry.Name}";
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entry.Value.GetRawText() + Rules)));
            var known = await db.PublishedDocs.FindAsync([key], ct);
            if (known?.Hash == hash)
            {
                continue;
            }

            var current = existing.GetValueOrDefault(entry.Name);
            var fields = Missing(entry.Value.GetProperty("info"), current);

            if (fields.Count > 0)
            {
                // The year the event list files the info under: the entry's own for an event only the
                // WSDC calendar has, else the scoring.dance event's.
                var knownYear = entry.Value.TryGetProperty("year", out var y) && y.TryGetInt32(out var given) ? given
                    : years.TryGetValue(entry.Name, out var scored) ? scored : (int?)null;
                if (!Has(current, "year") && knownYear is { } year)
                {
                    fields["year"] = (long)year;
                }

                fields["autofill"] = new Dictionary<string, object?>
                {
                    ["source"] = entry.Value.GetProperty("source").GetString(),
                    ["on"] = entry.Value.GetProperty("on").GetString(),
                    ["fields"] = Autofilled(current).Concat(fields.Keys.Where(k => k != "year")).Distinct().ToList<object?>(),
                };
                fields["updatedAt"] = FieldValue.ServerTimestamp;

                await firestore.Document($"info/{entry.Name}").SetAsync(fields, SetOptions.MergeAll, ct);
                applied++;
                LogFilled(logger, entry.Name, string.Join(", ", fields.Keys));
            }

            if (known is null)
            {
                db.PublishedDocs.Add(new PublishedDoc { Path = key, Hash = hash, PublishedUtc = clock.GetUtcNow().UtcDateTime });
            }
            else
            {
                known.Hash = hash;
                known.PublishedUtc = clock.GetUtcNow().UtcDateTime;
            }

            await db.SaveChangesAsync(ct);
        }

        await RepairYearsAsync(existing, ct);
        LogDone(logger, applied);
    }

    /// <summary>
    /// The event list finds an event's details by the <c>year</c> they carry. Details saved for an event
    /// that scoring.dance does not list (a WSDC calendar edition) once went in without one and never
    /// showed on the list; the year the published event itself has is copied over.
    /// </summary>
    private async Task RepairYearsAsync(IReadOnlyDictionary<string, JsonElement> existing, CancellationToken ct)
    {
        foreach (var id in existing.Where(i => !Has(i.Value, "year")).Select(i => i.Key))
        {
            var published = await firestore.Document($"events/{id}").GetSnapshotAsync(ct);
            if (published.Exists && published.TryGetValue<long>("year", out var year))
            {
                await firestore.Document($"info/{id}").SetAsync(new Dictionary<string, object> { ["year"] = year }, SetOptions.MergeAll, ct);
                LogRepaired(logger, id, year);
            }
        }
    }

    /// <summary>The suggested fields the admin's document has no value for, as Firestore values.</summary>
    internal static Dictionary<string, object?> Missing(JsonElement suggested, JsonElement? current)
    {
        Dictionary<string, object?> fields = [];
        var untouched = HasNote(current);
        foreach (var p in suggested.EnumerateObject())
        {
            if (p.Value.ValueKind is JsonValueKind.Object && current is { ValueKind: JsonValueKind.Object } doc && doc.TryGetProperty(p.Name, out var existing) && existing.ValueKind is JsonValueKind.Object)
            {
                // An object the admin already started (an override with a corrected city) gets only its missing parts.
                if (Missing(p.Value, existing) is { Count: > 0 } missing)
                {
                    fields[p.Name] = missing;
                }
            }
            else if (!Has(current, p.Name) || untouched)
            {
                fields[p.Name] = FirestoreStore.ToValue(p.Value);
            }
        }

        return fields;
    }

    private static bool HasNote(JsonElement? doc) =>
        doc is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty("autofill", out var note) && note.ValueKind is JsonValueKind.Object;

    /// <summary>The fields the document's autofill note says were filled in by a sync, not typed by the admin.</summary>
    private static HashSet<string> Autofilled(JsonElement? doc) =>
        doc is { ValueKind: JsonValueKind.Object } d
            && d.TryGetProperty("autofill", out var note) && note.ValueKind is JsonValueKind.Object
            && note.TryGetProperty("fields", out var named) && named.ValueKind is JsonValueKind.Array
            ? [.. named.EnumerateArray().Where(f => f.ValueKind is JsonValueKind.String).Select(f => f.GetString()!)]
            : [];

    private static bool Has(JsonElement? doc, string name) =>
        doc is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty(name, out var v) && v.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => false,
            JsonValueKind.String => v.GetString()!.Trim().Length > 0,
            JsonValueKind.Array => v.GetArrayLength() > 0,
            JsonValueKind.Object => v.EnumerateObject().Any(x => x.Value.ValueKind is not JsonValueKind.Null && !(x.Value.ValueKind is JsonValueKind.String && x.Value.GetString() is "")),
            _ => true,
        };

    [LoggerMessage(Level = LogLevel.Information, Message = "Autofilled info/{Id}: {Fields}")]
    private static partial void LogFilled(ILogger logger, string id, string fields);

    [LoggerMessage(Level = LogLevel.Information, Message = "Autofilled info/{Id}: year {Year} added so the event list finds it")]
    private static partial void LogRepaired(ILogger logger, string id, long year);

    [LoggerMessage(Level = LogLevel.Information, Message = "Autofill: {Count} events filled")]
    private static partial void LogDone(ILogger logger, int count);
}
