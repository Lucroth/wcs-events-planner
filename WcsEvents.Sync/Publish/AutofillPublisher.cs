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
/// until the admin saves the event.
/// </summary>
public sealed partial class AutofillPublisher(FirestoreDb firestore, FirestoreStore store, AppDbContext db, TimeProvider clock, ILogger<AutofillPublisher> logger)
{
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
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entry.Value.GetRawText())));
            var known = await db.PublishedDocs.FindAsync([key], ct);
            if (known?.Hash == hash)
            {
                continue;
            }

            var current = existing.GetValueOrDefault(entry.Name);
            var fields = Missing(entry.Value.GetProperty("info"), current);

            if (fields.Count > 0)
            {
                if (!Has(current, "year") && years.TryGetValue(entry.Name, out var year))
                {
                    fields["year"] = (long)year;
                }

                fields["autofill"] = new Dictionary<string, object?>
                {
                    ["source"] = entry.Value.GetProperty("source").GetString(),
                    ["on"] = entry.Value.GetProperty("on").GetString(),
                    ["fields"] = fields.Keys.Where(k => k != "year").ToList<object?>(),
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

        LogDone(logger, applied);
    }

    /// <summary>The suggested fields the admin's document has no value for, as Firestore values.</summary>
    internal static Dictionary<string, object?> Missing(JsonElement suggested, JsonElement? current)
    {
        Dictionary<string, object?> fields = [];
        foreach (var p in suggested.EnumerateObject())
        {
            if (!Has(current, p.Name))
            {
                fields[p.Name] = FirestoreStore.ToValue(p.Value);
            }
        }

        return fields;
    }

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

    [LoggerMessage(Level = LogLevel.Information, Message = "Autofill: {Count} events filled")]
    private static partial void LogDone(ILogger logger, int count);
}
