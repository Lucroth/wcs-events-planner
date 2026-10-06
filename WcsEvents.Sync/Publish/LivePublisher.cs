using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Cloud.Firestore;
using WcsEvents.Sync.Data;
using WcsEvents.Sync.Scoring;

namespace WcsEvents.Sync.Publish;

/// <summary>
/// Follows events running today on scoring.dance and keeps <c>live/{id}</c> current: the
/// competition schedule with each round's status (on the floor, being scored, finished), who was
/// called back from each finished round, and final placings. Pages listen to these documents, so an
/// open page updates by itself. Runs until its time budget ends, polling once a minute.
/// </summary>
public sealed partial class LivePublisher(FirestoreDb firestore, ScoringClient scoring, TimeProvider clock, ILogger<LivePublisher> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    /// <summary>Finished rounds' results by round id; published results do not change.</summary>
    private readonly Dictionary<int, LiveResult?> results = [];

    private readonly Dictionary<string, string> lastHash = [];

    public async Task PublishAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var ids = await LiveEventIdsAsync(ct);
                if (ids.Count is 0)
                {
                    LogNothingLive(logger);
                    return;
                }

                foreach (var id in ids)
                {
                    try
                    {
                        await PublishEventAsync(id, ct);
                    }
                    catch (Exception e) when (e is HttpRequestException or JsonException)
                    {
                        LogFailed(logger, id, e.Message);
                    }
                }

                await Task.Delay(Interval, clock, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The time budget ran out; the next scheduled run carries on.
        }
    }

    /// <summary>Published events whose dates include today (UTC, a day either side for time zones).</summary>
    private async Task<List<int>> LiveEventIdsAsync(CancellationToken ct)
    {
        // A manual run can name events to follow whatever their dates, to try this out.
        if (Environment.GetEnvironmentVariable("LIVE_EVENTS") is { Length: > 0 } forced)
        {
            return [.. forced.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(int.Parse)];
        }

        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        List<int> ids = [];

        foreach (var year in new[] { today.Year - 1, today.Year }.Distinct())
        {
            var doc = await firestore.Document($"years/{year}").GetSnapshotAsync(ct);
            if (!doc.Exists || !doc.TryGetValue<List<Dictionary<string, object>>>("events", out var events))
            {
                continue;
            }

            foreach (var e in events)
            {
                if (e.GetValueOrDefault("expected") is true
                    || !int.TryParse(e.GetValueOrDefault("id") as string, out var id)
                    || !DateOnly.TryParse(e.GetValueOrDefault("dateFrom") as string, out var from)
                    || !DateOnly.TryParse(e.GetValueOrDefault("dateTo") as string, out var to))
                {
                    continue;
                }

                if (IsLive(from, to, today))
                {
                    ids.Add(id);
                }
            }
        }

        return ids;
    }

    internal static bool IsLive(DateOnly from, DateOnly to, DateOnly today) =>
        from.AddDays(-1) <= today && today <= to.AddDays(1);

    private async Task PublishEventAsync(int id, CancellationToken ct)
    {
        var schedule = await scoring.GetScheduleJsonAsync(id, ct);
        if (schedule is null)
        {
            return;
        }

        var wall = await scoring.GetWallAsync(id, ct);
        var roundIds = wall is null
            ? []
            : ScoringParser.ParseWallRounds(wall).GroupBy(r => r.Name).ToDictionary(g => g.Key, g => g.First().RoundId);

        List<object> rounds = [];
        foreach (var item in ParseSchedule(schedule))
        {
            int? roundId = roundIds.TryGetValue(item.Name, out var rid) ? rid : null;
            LiveResult? result = null;
            if (roundId is { } r && item.Status == 99)
            {
                if (!results.TryGetValue(r, out result))
                {
                    var html = await scoring.GetRoundAsync(id, r, ct);
                    result = html is null ? null : Result(ScoringParser.ParseRound(html));
                    results[r] = result;
                }
            }

            rounds.Add(new
            {
                item.Name,
                item.Day,
                item.Time,
                item.Status,
                Label = Label(item.Status),
                RoundId = roundId,
                Advanced = result?.Advanced,
                Placements = result?.Placements,
            });
        }

        var doc = new { EventId = id.ToString(), Rounds = rounds };
        var json = JsonSerializer.Serialize(doc, Json);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        var path = $"live/{id}";
        if (lastHash.GetValueOrDefault(path) == hash)
        {
            return;
        }

        using var parsed = JsonDocument.Parse(json);
        var fields = (Dictionary<string, object?>)FirestoreStore.ToValue(parsed.RootElement)!;
        fields["updatedAt"] = FieldValue.ServerTimestamp;
        await firestore.Document(path).SetAsync(fields, cancellationToken: ct);
        lastHash[path] = hash;
        LogPublished(logger, id, rounds.Count);
    }

    internal sealed record ScheduleItem(string Name, string? Day, string? Time, int Status);

    /// <summary>The wall's schedule JSON; a row's day is only set on the first round of each day.</summary>
    internal static List<ScheduleItem> ParseSchedule(string json)
    {
        using var doc = JsonDocument.Parse(json);
        List<ScheduleItem> items = [];
        string? day = null;

        foreach (var row in doc.RootElement.EnumerateArray())
        {
            var name = Text(row, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (Text(row, "dayname") is { Length: > 0 } d)
            {
                day = d;
            }

            var hidden = Text(row, "time_start_hidden") is "1";
            items.Add(new ScheduleItem(
                WebUtility(name),
                day,
                hidden || Text(row, "time") is not { Length: > 0 } t ? null : t,
                int.TryParse(Text(row, "status"), out var s) ? s : 0));
        }

        return items;

        static string? Text(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                _ => null,
            } : null;

        static string WebUtility(string s) => System.Net.WebUtility.HtmlDecode(s).Trim();
    }

    /// <summary>What the wall's status icons mean, in words.</summary>
    internal static string Label(int status) => status switch
    {
        99 => "Finished",
        11 => "Head judge checking",
        >= 9 and < 11 => "Scoring",
        8 => "Results being published",
        7 => "On the floor",
        6 => "Ready",
        5 => "Lining up",
        4 => "Start list out",
        _ => "Coming up",
    };

    internal sealed record LiveResult(List<List<string>>? Advanced, List<object>? Placements);

    /// <summary>
    /// A finished round: its callbacks, one list per published table (a prelim usually lists leaders
    /// and followers apart), or for a final its placings, couples joined as in the results card.
    /// </summary>
    internal static LiveResult? Result(ParsedRound? round)
    {
        if (round is null || round.Entries.Count is 0)
        {
            return null;
        }

        if (ScoringParser.KindOf(round.RoundName) is RoundKind.Final)
        {
            List<object> placements = [.. round.Entries
                .Where(e => !e.IsScratched)
                .GroupBy(e => e.Position)
                .OrderBy(g => g.Key)
                .Select(g => new
                {
                    Position = g.Key,
                    Names = string.Join(" & ", g.OrderBy(e => e.Role is null ? 2 : (int)e.Role).Select(e => e.Name)),
                })];
            return new LiveResult(null, placements);
        }

        List<List<string>> advanced = [.. round.Entries
            .Where(e => e.Advanced && !e.IsScratched)
            .GroupBy(e => e.TableIndex)
            .OrderBy(g => g.Key)
            .Select(g => g.OrderBy(e => e.Position).Select(e => e.Name).Distinct().ToList())];
        return advanced.Count is 0 ? null : new LiveResult(advanced, null);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "No event is live today")]
    private static partial void LogNothingLive(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Live {Id}: {Rounds} rounds")]
    private static partial void LogPublished(ILogger logger, int id, int rounds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Live {Id} failed: {Error}")]
    private static partial void LogFailed(ILogger logger, int id, string error);
}
