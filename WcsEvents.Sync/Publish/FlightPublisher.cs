using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WcsEvents.Sync.Data;
using WcsEvents.Sync.Scoring;
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

        var scraped = await db.ScoringEvents.AsNoTracking()
            .Where(e => e.DateFrom != null && e.Name != "")
            .ToListAsync(ct);

        // Hand-made events live only in Firestore; they need fares as much as scraped ones.
        var manual = await store.ReadWhereAsync("events", "manual", true, ct);

        List<Trip> trips =
        [
            .. scraped.Select(e => Trip.Of(e.Id.ToString(), e.DateFrom!.Value, e.DateTo ?? e.DateFrom!.Value, e.City, e.Country, infos)),
            .. manual.Select(m => Trip.Of(m.Key, Date(m.Value, "dateFrom"), Date(m.Value, "dateTo"), Str(m.Value, "city"), Str(m.Value, "country"), infos)),
        ];

        var upcoming = trips
            .Where(t => t.Start > today && t.Start <= today.AddDays(HorizonDays))
            .Where(t => t.Country is not "Poland" && Countries.IsEuropean(t.Country))
            .ToList();

        var origins = HomeCities.All.Select(c => c.FlightOrigins).DistinctBy(HomeCities.Key).ToList();
        var searched = 0;

        foreach (var (id, start, end, city, country, info) in upcoming)
        {
            var travel = await EventPublisher.TravelAsync(city, country, info, places, ct);
            if (travel.Airports.Count is 0)
            {
                continue;
            }

            List<string> destinations = [.. travel.Airports.Select(a => a.Iata)];

            foreach (var from in origins)
            {
                var results = await search.SearchAsync(from, destinations, start, end, ct);
                searched++;

                await store.SetIfChangedAsync($"flights/{id}_{HomeCities.Key(from)}", new
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

    /// <summary>One event's dates and place with the admin's corrections applied.</summary>
    private sealed record Trip(string Id, DateOnly Start, DateOnly End, string? City, string? Country, AdminInfo Info)
    {
        public static Trip Of(string id, DateOnly from, DateOnly to, string? city, string? country, IReadOnlyDictionary<string, AdminInfo> infos)
        {
            var info = infos.GetValueOrDefault(id) ?? AdminInfo.Empty;
            return new Trip(id, info.DateFrom ?? from, info.DateTo ?? info.DateFrom ?? to, info.City ?? city, info.Country ?? country, info);
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String ? v.GetString() : null;

    private static DateOnly Date(JsonElement e, string name) =>
        DateOnly.TryParseExact(Str(e, name), "yyyy-MM-dd", out var d) ? d : DateOnly.MinValue;

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
