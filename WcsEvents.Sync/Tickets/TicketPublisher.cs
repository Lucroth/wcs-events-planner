using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Cloud.Firestore;
using WcsEvents.Sync.Data;
using WcsEvents.Sync.Publish;

namespace WcsEvents.Sync.Tickets;

/// <summary>
/// Keeps the passes of events that sell on DanceApp current: every upcoming event is paired with its
/// DanceApp page, and its pass tiers (with deadlines and the tier on sale now) are written to
/// <c>info/{id}.passes</c>. An event whose details an admin has saved is left alone: saving drops the
/// <c>autofill</c> marker, which is what says the passes were not typed by a person.
/// </summary>
public sealed partial class TicketPublisher(DanceAppClient danceApp, FirestoreDb firestore, FirestoreStore store, AppDbContext db, TimeProvider clock, ILogger<TicketPublisher> logger)
{
    public async Task PublishAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var events = await UpcomingAsync(today, ct);
        var list = await danceApp.ListAsync(ct);
        var infos = await store.ReadAllAsync("info", ct);
        var matches = DanceAppClient.Match(events, list);

        var written = 0;
        foreach (var (eventId, danceAppId) in matches)
        {
            if (await danceApp.TicketsAsync(danceAppId, ct) is not var (currency, offers)
                || DanceAppClient.Passes(currency, offers) is not { Count: > 0 } passes)
            {
                continue;
            }

            JsonElement? current = infos.TryGetValue(eventId, out var found) ? found : null;
            if (current is { ValueKind: JsonValueKind.Object } doc && HasPasses(doc) && !doc.TryGetProperty("autofill", out _))
            {
                continue;
            }

            var docs = passes.Select(p => new Dictionary<string, object?>
            {
                ["kind"] = p.Kind,
                ["tier"] = p.Tier,
                ["price"] = (double)p.Price,
                ["currency"] = p.Currency,
                ["until"] = p.Until?.ToString("yyyy-MM-dd"),
                ["current"] = p.Current,
                ["soldOut"] = p.SoldOut,
            }).ToList();

            var key = $"{firestore.ProjectId}/tickets/{eventId}";
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(docs))));
            var known = await db.PublishedDocs.FindAsync([key], ct);
            if (known?.Hash == hash)
            {
                continue;
            }

            var fields = new Dictionary<string, object?>
            {
                ["passes"] = docs,
                ["autofill"] = new Dictionary<string, object?>
                {
                    ["source"] = $"https://danceapp.net/en/events/{danceAppId}/register",
                    ["on"] = today.ToString("yyyy-MM-dd"),
                    ["fields"] = Fields(current),
                },
                ["updatedAt"] = FieldValue.ServerTimestamp,
            };
            if (current is not { } existing || !existing.TryGetProperty("year", out _))
            {
                fields["year"] = (long)events.First(e => e.Id == eventId).From.Year;
            }

            await firestore.Document($"info/{eventId}").SetAsync(fields, SetOptions.MergeAll, ct);

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
            written++;
        }

        LogDone(logger, events.Count, matches.Count, written);
    }

    /// <summary>The upcoming events of the year summaries the event list reads, expected-edition guesses excluded.</summary>
    private async Task<List<(string Id, string Name, DateOnly From, DateOnly To)>> UpcomingAsync(DateOnly today, CancellationToken ct)
    {
        List<(string, string, DateOnly, DateOnly)> events = [];
        foreach (var year in Enumerable.Range(today.Year, 3))
        {
            var doc = await firestore.Document($"years/{year}").GetSnapshotAsync(ct);
            if (!doc.Exists || !doc.TryGetValue<List<Dictionary<string, object>>>("events", out var rows))
            {
                continue;
            }

            foreach (var e in rows)
            {
                if (e.GetValueOrDefault("expected") is true
                    || e.GetValueOrDefault("id") is not string id
                    || e.GetValueOrDefault("name") is not string name
                    || !DateOnly.TryParse(e.GetValueOrDefault("dateFrom") as string, out var from)
                    || !DateOnly.TryParse(e.GetValueOrDefault("dateTo") as string, out var to)
                    || to < today)
                {
                    continue;
                }

                events.Add((id, name, from, to));
            }
        }

        return events;
    }

    private static bool HasPasses(JsonElement doc) =>
        doc.TryGetProperty("passes", out var p) && p.ValueKind is JsonValueKind.Array && p.GetArrayLength() > 0;

    /// <summary>The fields the marker already names, plus passes.</summary>
    private static List<object?> Fields(JsonElement? current)
    {
        List<object?> fields = [];
        if (current is { ValueKind: JsonValueKind.Object } doc
            && doc.TryGetProperty("autofill", out var marker)
            && marker.ValueKind is JsonValueKind.Object
            && marker.TryGetProperty("fields", out var named)
            && named.ValueKind is JsonValueKind.Array)
        {
            fields.AddRange(named.EnumerateArray().Where(f => f.ValueKind is JsonValueKind.String && f.GetString() != "passes").Select(f => (object?)f.GetString()));
        }

        fields.Add("passes");
        return fields;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "DanceApp passes: {Events} upcoming events, {Matched} found on DanceApp, {Written} updated")]
    private static partial void LogDone(ILogger logger, int events, int matched, int written);
}
