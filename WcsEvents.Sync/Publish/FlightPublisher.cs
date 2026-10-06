using Microsoft.EntityFrameworkCore;
using WcsEvents.Sync.Data;
using WcsEvents.Sync.Travel;

namespace WcsEvents.Sync.Publish;

/// <summary>
/// Fetches fares for every upcoming event abroad from every set of Polish home airports, and writes
/// them to <c>flights/{eventId}_{origins}</c>. Run daily: a static site cannot call the airlines
/// itself, since neither allows cross-origin requests.
/// </summary>
public sealed partial class FlightPublisher(
    AppDbContext db, Places places, FlightSearch search, FirestoreStore store, TimeProvider time, ILogger<FlightPublisher> logger)
{
    /// <summary>Airlines rarely sell further ahead than this, and searching further only spends requests.</summary>
    private const int HorizonDays = 240;

    public async Task PublishAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var infos = await EventPublisher.AdminInfosAsync(store, ct);

        var events = await db.ScoringEvents.AsNoTracking()
            .Where(e => e.DateFrom != null && e.Name != "")
            .ToListAsync(ct);

        var upcoming = events
            .Select(e => (Event: e, Info: infos.GetValueOrDefault(e.Id.ToString()) ?? AdminInfo.Empty))
            .Select(x => (x.Event, x.Info, Start: x.Info.DateFrom ?? x.Event.DateFrom!.Value, End: x.Info.DateTo ?? x.Event.DateTo ?? x.Event.DateFrom!.Value))
            .Where(x => x.Start > today && x.Start <= today.AddDays(HorizonDays))
            .Where(x => (x.Info.Country ?? x.Event.Country) is not "Poland")
            .ToList();

        var origins = HomeCities.All.Select(c => c.FlightOrigins).DistinctBy(HomeCities.Key).ToList();
        var searched = 0;

        foreach (var (e, info, start, end) in upcoming)
        {
            var travel = await EventPublisher.TravelAsync(e, info, places, ct);
            if (travel.Airports.Count is 0)
            {
                continue;
            }

            List<string> destinations = [.. travel.Airports.Select(a => a.Iata)];

            foreach (var from in origins)
            {
                var results = await search.SearchAsync(from, destinations, start, end, ct);
                searched++;

                await store.SetIfChangedAsync($"flights/{e.Id}_{HomeCities.Key(from)}", new
                {
                    Origins = from,
                    Destinations = destinations,
                    Currency = FlightSearch.Currency,
                    Combos = results.Combos.Select(c => new { Out = Leg(c.Out), Back = Leg(c.Back), c.PerPerson }),
                    Out = results.Out.Take(15).Select(Leg),
                    Back = results.Back.Take(15).Select(Leg),
                    FetchedOn = today.ToString("yyyy-MM-dd"),
                }, ct);
            }
        }

        LogPublished(logger, upcoming.Count, searched, store.Written, store.Skipped);
    }

    private static object Leg(FlightLeg l) => new
    {
        Airline = l.Airline.ToString(),
        l.From,
        l.To,
        Date = l.Date.ToString("yyyy-MM-dd"),
        Times = l.Times.Select(t => t.ToString("HH:mm")),
        Arrival = l.Arrival?.ToString("HH:mm"),
        DurationMinutes = l.Duration is { } d ? (int?)d.TotalMinutes : null,
        l.Price,
        l.Currency,
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Flights for {Events} upcoming events abroad: {Searches} searches, {Written} documents written, {Skipped} unchanged")]
    private static partial void LogPublished(ILogger logger, int events, int searches, int written, int skipped);
}
