using Microsoft.EntityFrameworkCore;
using WcsEvents.Sync.Data;

namespace WcsEvents.Sync.Metrics;

/// <summary>One dancer of a placed couple; <paramref name="Wscid"/> is set once they hold WSDC points, and <paramref name="Points"/> is what the registry credits them for this result.</summary>
public sealed record Finalist(string Name, int? Wscid, int? Points = null);

/// <summary>A registry result of one dancer in one division, for matching to a finalist.</summary>
public sealed record RegistryResult(string EventName, DateOnly? Date, int Points, int? Rank);

public sealed record FinalPlace(int Position, string Names, IReadOnlyList<Finalist> Dancers);

public sealed record DivisionResult(string Division, string RoundName, int RoundId, IReadOnlyList<FinalPlace> Places);

public sealed record EditionRef(int Id, string Name, DateOnly? DateFrom);

/// <summary>Everything the mirror knows about one event, ready to publish.</summary>
public sealed record EventFacts(
    ScoringEvent Event,
    bool IsWsdc,
    IReadOnlyList<DivisionStrength> Strengths,
    EditionRef? StrengthsFrom,
    IReadOnlyList<DivisionResult> Results);

/// <summary>
/// Builds the per-event facts from the mirror: whether the event awards WSDC points, how strong its
/// fields were (or its previous edition's, for one not yet held) and its Jack &amp; Jill finals.
/// </summary>
public sealed class EventCatalog(AppDbContext db, Strength strength)
{
    public async Task<IReadOnlyList<EventFacts>> AllAsync(CancellationToken ct)
    {
        var events = await db.ScoringEvents.AsNoTracking()
            .Where(e => e.DateFrom != null && e.Name != "")
            .OrderBy(e => e.DateFrom)
            .ToListAsync(ct);

        var strengths = await strength.AllAsync(ct);
        List<ScoringEvent> withResults = [.. events.Where(e => strengths.ContainsKey(e.Id))];
        var registry = await RegistryEventsAsync(ct);
        var results = await ResultsAsync(events, ct);

        return [.. events.Select(e =>
        {
            var own = strengths.GetValueOrDefault(e.Id);
            var previous = own is null ? PreviousEdition(e, withResults) : null;

            return new EventFacts(
                e,
                e.IsWsdc || InRegistry(e, registry),
                own ?? (previous is null ? [] : strengths[previous.Id]),
                previous is null ? null : new EditionRef(previous.Id, previous.Name, previous.DateFrom),
                results.GetValueOrDefault(e.Id) ?? []);
        })];
    }

    /// <summary>Jack &amp; Jill finals, one per division, couples listed as they placed.</summary>
    private async Task<Dictionary<int, IReadOnlyList<DivisionResult>>> ResultsAsync(IReadOnlyList<ScoringEvent> events, CancellationToken ct)
    {
        var eventOf = events.ToDictionary(e => e.Id);
        var entries = await db.ScoringEntries.AsNoTracking()
            .Where(e => e.Round.IsJackAndJill && e.Round.Kind == RoundKind.Final && e.Round.DivisionAbbreviation != null && !e.IsScratched)
            .Select(e => new { e.Round.ScoringEventId, e.RoundId, e.Round.RoundName, Division = e.Round.DivisionAbbreviation!, e.Position, e.Name, e.Wscid, e.Role, e.TableIndex })
            .ToListAsync(ct);

        var registry = await RegistryResultsAsync([.. entries.Where(e => e.Wscid != null).Select(e => e.Wscid!.Value).Distinct()], ct);

        return entries
            .GroupBy(e => e.ScoringEventId)
            .ToDictionary(
                night => night.Key,
                night => (IReadOnlyList<DivisionResult>)[.. night
                    .GroupBy(e => (e.RoundId, e.RoundName, e.Division))
                    .Select(g => new DivisionResult(g.Key.Division, g.Key.RoundName, g.Key.RoundId, [.. g
                        .GroupBy(e => e.Position)
                        .OrderBy(p => p.Key)
                        .Select(p =>
                        {
                            List<Finalist> couple = [.. p
                                .OrderBy(x => x.Role is null ? 2 : (int)x.Role)
                                .ThenBy(x => x.TableIndex)
                                .Select(x => new Finalist(x.Name, x.Wscid, x.Wscid is { } id && eventOf.TryGetValue(night.Key, out var held)
                                    ? PointsFor(registry.GetValueOrDefault((id, g.Key.Division)), held, p.Key)
                                    : null))];
                            return new FinalPlace(p.Key, string.Join(" & ", couple.Select(d => d.Name)), couple);
                        })]))
                    .OrderBy(r => Strength.DivisionOrder(r.Division))]);
    }

    /// <summary>Registry results of the given dancers, by dancer and division, from the earliest event the mirror has.</summary>
    private async Task<Dictionary<(int Wscid, string Division), List<RegistryResult>>> RegistryResultsAsync(List<int> wscids, CancellationToken ct)
    {
        Dictionary<(int, string), List<RegistryResult>> byDancer = [];
        foreach (var chunk in wscids.Chunk(2000))
        {
            var rows = await db.Placements.AsNoTracking()
                .Where(p => chunk.Contains(p.DancerWscid))
                .Select(p => new { p.DancerWscid, Division = p.Division.Abbreviation, p.EventName, p.Date, p.Points, p.Rank })
                .ToListAsync(ct);

            foreach (var row in rows)
            {
                var key = (row.DancerWscid, row.Division);
                if (!byDancer.TryGetValue(key, out var list))
                {
                    byDancer[key] = list = [];
                }

                list.Add(new RegistryResult(row.EventName, row.Date, row.Points, row.Rank));
            }
        }

        return byDancer;
    }

    /// <summary>
    /// What the registry credits a dancer for this event's final: the result of the same-named event
    /// within a month of it (the registry dates results by month), the one whose finish matches the
    /// placing when the dancer has several; null when the registry has none yet.
    /// </summary>
    internal static int? PointsFor(IReadOnlyList<RegistryResult>? results, ScoringEvent held, int position)
    {
        if (results is null || held.DateFrom is not { } day)
        {
            return null;
        }

        var month = new DateOnly(day.Year, day.Month, 1);
        List<RegistryResult> same = [.. results.Where(r =>
            r.Date is { } d && Math.Abs(((d.Year - month.Year) * 12) + d.Month - month.Month) <= 1 && EventMatching.SameName(r.EventName, held.Name))];

        return (same.FirstOrDefault(r => r.Rank == position) ?? same.FirstOrDefault())?.Points;
    }

    /// <summary>Event names the registry holds placements for, by month. The registry lists only
    /// WSDC-sanctioned events, and the listing that carries the flag drops events once they run.</summary>
    private async Task<ILookup<(int Year, int Month), string>> RegistryEventsAsync(CancellationToken ct)
    {
        var rows = await db.Placements.AsNoTracking()
            .Where(p => p.Date != null)
            .Select(p => new { p.EventName, p.Date })
            .Distinct()
            .ToListAsync(ct);

        return rows.ToLookup(r => (r.Date!.Value.Year, r.Date.Value.Month), r => r.EventName);
    }

    internal static ScoringEvent? PreviousEdition(ScoringEvent e, IEnumerable<ScoringEvent> withResults) =>
        withResults
            .Where(p => p.Id != e.Id && p.DateFrom < (e.DateFrom ?? DateOnly.MaxValue) && EventMatching.SameName(p.Name, e.Name))
            .MaxBy(p => p.DateFrom);

    private static bool InRegistry(ScoringEvent e, ILookup<(int, int), string> registry)
    {
        if (e.DateFrom is not { } d)
        {
            return false;
        }

        for (var offset = -1; offset <= 1; offset++)
        {
            var m = d.AddMonths(offset);
            if (registry[(m.Year, m.Month)].Any(name => EventMatching.SameName(name, e.Name)))
            {
                return true;
            }
        }

        return false;
    }
}
