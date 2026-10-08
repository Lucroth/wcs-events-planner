using System.Globalization;
using System.Text.Json;
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
        var trips = await Trip.LoadAsync(db, store, ct);

        var upcoming = trips
            .Where(t => t.Start > today && t.Start <= today.AddDays(HorizonDays))
            .Where(t => t.Country is not "Poland" && Countries.IsEuropean(t.Country))
            .ToList();

        var origins = HomeCities.All.Select(c => c.FlightOrigins).DistinctBy(HomeCities.Key).ToList();
        var searched = 0;

        // WIZZ=reuse (the cloud job): keep the Wizz fares a home run stored rather than ask Wizz,
        // which refuses data-centre networks.
        var stored = Environment.GetEnvironmentVariable("WIZZ") is "reuse" ? await store.ReadAllAsync("flights", ct) : null;

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
                var path = $"{id}_{HomeCities.Key(from)}";
                var knownWizz = stored is null ? null : StoredWizz(stored.TryGetValue(path, out var doc) ? doc : null);
                var results = await search.SearchAsync(from, destinations, start, end, ct, knownWizz);
                searched++;

                await store.SetIfChangedAsync($"flights/{id}_{HomeCities.Key(from)}", new
                {
                    EventId = id,
                    Key = HomeCities.Key(from),
                    Cheapest = results.Combos.Count > 0 ? results.Combos[0].PerPerson : (decimal?)null,
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

    /// <summary>The Wizz legs a flights document holds, from its leg lists and its combinations.</summary>
    internal static IReadOnlyList<FlightLeg> StoredWizz(JsonElement? doc)
    {
        if (doc is not { ValueKind: JsonValueKind.Object } d)
        {
            return [];
        }

        IEnumerable<JsonElement> Legs(string name) =>
            d.TryGetProperty(name, out var list) && list.ValueKind is JsonValueKind.Array ? list.EnumerateArray() : [];

        var fromCombos = Legs("combos").SelectMany(c => new[] { c.GetProperty("out"), c.GetProperty("back") });
        return [.. Legs("out").Concat(Legs("back")).Concat(fromCombos)
            .Where(l => l.GetProperty("airline").GetString() == nameof(Airline.Wizz))
            .Select(l => new FlightLeg(
                Airline.Wizz,
                l.GetProperty("from").GetString()!,
                l.GetProperty("to").GetString()!,
                DateOnly.ParseExact(l.GetProperty("date").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                [.. l.GetProperty("times").EnumerateArray().Select(t => TimeOnly.ParseExact(t.GetString()!, "HH:mm", CultureInfo.InvariantCulture))],
                null,
                null,
                l.GetProperty("price").GetDecimal(),
                l.GetProperty("currency").GetString()!))
            .DistinctBy(l => (l.From, l.To, l.Date, l.Price))];
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
