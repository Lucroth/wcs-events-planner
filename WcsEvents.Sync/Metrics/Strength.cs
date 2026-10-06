using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using WcsEvents.Sync.Data;
using WcsEvents.Sync.Scoring;

namespace WcsEvents.Sync.Metrics;

public enum Difficulty
{
    Easy,
    Medium,
    Hard,
}

/// <summary>
/// One side of one division's Jack &amp; Jill at one event: how many danced it and how many points they
/// held coming in. Taken from the round with the largest field, normally the prelim.
/// </summary>
public sealed record DivisionStrength(
    string Division, Role Role, string RoundName, int FieldSize, double AveragePoints, double MedianPoints,
    double TopQuartileAverage, Difficulty? Difficulty, double EuropeTopQuartileAverage = 0);

/// <summary>
/// How strong each event's fields were, and so how hard it is to final there. Points are those each
/// entrant held in the division <em>at the time</em>, rebuilt from registry placements dated in
/// earlier months, the same reconstruction wsdc-stats uses: today's totals would mostly measure how
/// long ago an event was. Difficulty ranks an event's top-quartile average (the strongest quarter of
/// the field) against every other event's in the same division and role, in thirds, among European events only: the plain
/// average is mostly a count of newcomers with no points, while the top quarter is who has to be
/// beaten to make the final.
/// </summary>
public sealed class Strength(AppDbContext db, IMemoryCache cache)
{
    private const string CacheKey = "strength:all";

    public async Task<IReadOnlyList<DivisionStrength>> ForEventAsync(int scoringEventId, CancellationToken ct) =>
        (await AllAsync(ct)).GetValueOrDefault(scoringEventId) ?? [];

    /// <summary>Every event's division strengths, keyed by scoring.dance event id. Cached for six hours: it only changes after a sync.</summary>
    public async Task<IReadOnlyDictionary<int, IReadOnlyList<DivisionStrength>>> AllAsync(CancellationToken ct)
    {
        if (cache.Get<IReadOnlyDictionary<int, IReadOnlyList<DivisionStrength>>>(CacheKey) is { } hit)
        {
            return hit;
        }

        var divisions = await db.ScoringRounds.AsNoTracking()
            .Where(r => r.IsJackAndJill && r.DivisionAbbreviation != null)
            .Select(r => r.DivisionAbbreviation!)
            .Distinct()
            .ToListAsync(ct);

        var european = await EuropeanEventIdsAsync(ct);
        List<(int EventId, DivisionStrength Strength)> rows = [];

        foreach (var division in divisions)
        {
            foreach (var role in (Role[])[Role.Leader, Role.Follower])
            {
                rows.AddRange(await ForDivisionAsync(division, role, european, ct));
            }
        }

        IReadOnlyDictionary<int, IReadOnlyList<DivisionStrength>> result = rows
            .GroupBy(r => r.EventId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<DivisionStrength>)[.. g.Select(x => x.Strength).OrderBy(s => DivisionOrder(s.Division)).ThenBy(s => s.Role)]);

        cache.Set(CacheKey, result, TimeSpan.FromHours(6));
        return result;
    }

    /// <summary>
    /// The app covers European events only, so levels are ranked among them alone. The event's own
    /// country is preferred; a round's is the fallback for events the listing never carried.
    /// </summary>
    private async Task<HashSet<int>> EuropeanEventIdsAsync(CancellationToken ct)
    {
        var events = await db.ScoringEvents.AsNoTracking().Select(e => new { e.Id, e.Country }).ToListAsync(ct);
        var rounds = await db.ScoringRounds.AsNoTracking().Select(r => new { r.ScoringEventId, r.Country }).Distinct().ToListAsync(ct);
        var byEvent = events.ToDictionary(e => e.Id, e => e.Country);

        return [.. rounds
            .Select(r => (Id: r.ScoringEventId, Country: byEvent.GetValueOrDefault(r.ScoringEventId) ?? r.Country))
            .Concat(events.Select(e => (e.Id, e.Country)))
            .Where(x => Countries.IsEuropean(x.Country))
            .Select(x => x.Id)];
    }

    private async Task<List<(int EventId, DivisionStrength Strength)>> ForDivisionAsync(
        string division, Role role, IReadOnlySet<int> european, CancellationToken ct)
    {
        var entrants = await db.ScoringEntries.AsNoTracking()
            .Where(e => e.Round.DivisionAbbreviation == division && e.Round.IsJackAndJill && !e.IsScratched && e.Role == role)
            .Select(e => new { e.RoundId, e.Round.ScoringEventId, e.Round.RoundName, e.Wscid, e.Round.EventDate })
            .ToListAsync(ct);
        entrants.RemoveAll(e => !european.Contains(e.ScoringEventId));

        var placements = await db.Placements.AsNoTracking()
            .Where(p => p.Division.Abbreviation == division && p.Role == role)
            .Select(p => new { p.DancerWscid, p.Date, p.Points })
            .ToListAsync(ct);

        var history = new PointsHistory(placements.Select(p => (p.DancerWscid, p.Date, p.Points)));

        var perEvent = entrants
            .GroupBy(e => e.RoundId)
            .Select(g =>
            {
                var first = g.First();
                List<double> held = [.. g.Select(e => e.Wscid is { } id ? history.AsOf(id, first.EventDate) : 0).Order()];
                return (first.ScoringEventId, first.RoundName, Size: held.Count, Average: held.Average(), Median: Ranking.Median(held), Top: TopQuartileAverage(held));
            })
            .GroupBy(r => r.ScoringEventId)
            .Select(g => g.MaxBy(r => r.Size))
            .ToList();

        List<double> tops = [.. perEvent.Select(r => r.Top).Order()];

        return [.. perEvent.Select(r => (r.ScoringEventId, new DivisionStrength(
            division, role, r.RoundName, r.Size, r.Average, r.Median, r.Top, Classify(r.Top, tops), tops.Average())))];
    }

    /// <summary>Mean of the highest quarter of an ascending list, at least one value.</summary>
    internal static double TopQuartileAverage(IReadOnlyList<double> sortedAscending) =>
        sortedAscending.TakeLast(Math.Max(1, (int)Math.Ceiling(sortedAscending.Count / 4.0))).Average();

    /// <summary>Which third of <paramref name="sorted"/> a value falls in; null when there are too few events to say.</summary>
    internal static Difficulty? Classify(double value, IReadOnlyList<double> sorted)
    {
        if (sorted.Count < 3)
        {
            return null;
        }

        var below = sorted.Count(v => v < value);
        var share = (double)below / sorted.Count;

        return share switch
        {
            < 1 / 3.0 => Difficulty.Easy,
            < 2 / 3.0 => Difficulty.Medium,
            _ => Difficulty.Hard,
        };
    }

    public static int DivisionOrder(string abbreviation) => abbreviation switch
    {
        "NEW" => 0,
        "NOV" => 1,
        "INT" => 2,
        "ADV" => 3,
        "ALS" => 4,
        "CHMP" => 5,
        _ => 10,
    };

    /// <summary>
    /// Points a dancer held coming into a date: every dated placement from strictly earlier months,
    /// plus undated ones as an always-held baseline. Registry dates are the first of the month, so a
    /// placement in the event's own month may be the points that very event awarded.
    /// </summary>
    internal sealed class PointsHistory
    {
        private readonly Dictionary<int, int> baseline = [];
        private readonly Dictionary<int, List<(DateOnly Date, int Points)>> dated = [];

        public PointsHistory(IEnumerable<(int Wscid, DateOnly? Date, int Points)> placements)
        {
            foreach (var (wscid, date, points) in placements)
            {
                if (date is { } d)
                {
                    if (!dated.TryGetValue(wscid, out var list))
                    {
                        dated[wscid] = list = [];
                    }

                    list.Add((d, points));
                }
                else
                {
                    baseline[wscid] = baseline.GetValueOrDefault(wscid) + points;
                }
            }

            foreach (var list in dated.Values)
            {
                list.Sort((a, b) => a.Date.CompareTo(b.Date));
            }
        }

        public double AsOf(int wscid, DateOnly? asOf)
        {
            var sum = (double)baseline.GetValueOrDefault(wscid);
            if (!dated.TryGetValue(wscid, out var list))
            {
                return sum;
            }

            if (asOf is not { } day)
            {
                return sum + list.Sum(x => x.Points);
            }

            var monthStart = new DateOnly(day.Year, day.Month, 1);
            foreach (var (date, points) in list)
            {
                if (date >= monthStart)
                {
                    break;
                }

                sum += points;
            }

            return sum;
        }
    }
}
