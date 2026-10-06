using Microsoft.EntityFrameworkCore;
using WcsEvents.Sync.Data;

namespace WcsEvents.Sync.Metrics;

public sealed record FinalPlace(int Position, string Names);

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
        var results = await ResultsAsync(ct);

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
    private async Task<Dictionary<int, IReadOnlyList<DivisionResult>>> ResultsAsync(CancellationToken ct)
    {
        var entries = await db.ScoringEntries.AsNoTracking()
            .Where(e => e.Round.IsJackAndJill && e.Round.Kind == RoundKind.Final && e.Round.DivisionAbbreviation != null && !e.IsScratched)
            .Select(e => new { e.Round.ScoringEventId, e.RoundId, e.Round.RoundName, Division = e.Round.DivisionAbbreviation!, e.Position, e.Name, e.Role, e.TableIndex })
            .ToListAsync(ct);

        return entries
            .GroupBy(e => e.ScoringEventId)
            .ToDictionary(
                ev => ev.Key,
                ev => (IReadOnlyList<DivisionResult>)[.. ev
                    .GroupBy(e => (e.RoundId, e.RoundName, e.Division))
                    .Select(g => new DivisionResult(g.Key.Division, g.Key.RoundName, g.Key.RoundId, [.. g
                        .GroupBy(e => e.Position)
                        .OrderBy(p => p.Key)
                        .Select(p => new FinalPlace(p.Key, string.Join(" & ", p
                            .OrderBy(x => x.Role is null ? 2 : (int)x.Role)
                            .ThenBy(x => x.TableIndex)
                            .Select(x => x.Name))))]))
                    .OrderBy(r => Strength.DivisionOrder(r.Division))]);
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
