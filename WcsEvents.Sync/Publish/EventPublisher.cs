using WcsEvents.Sync.Metrics;
using WcsEvents.Sync.Travel;

namespace WcsEvents.Sync.Publish;

public sealed record TravelFacts((double Lat, double Lng)? Coords, IReadOnlyList<Airport> Airports, Station? Station);

/// <summary>
/// Publishes every event to <c>events/{id}</c> and one summary per year to <c>years/{year}</c>, the
/// single document the event list reads. Travel facts (where the event is, which airports and which
/// station serve it) are worked out only for events still to come.
/// </summary>
public sealed partial class EventPublisher(
    EventCatalog catalog, Places places, FirestoreStore store, TimeProvider time, ILogger<EventPublisher> logger)
{
    public async Task PublishAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var infos = await AdminInfosAsync(store, ct);
        var all = await catalog.AllAsync(ct);

        foreach (var facts in all)
        {
            var e = facts.Event;
            var info = infos.GetValueOrDefault(e.Id.ToString()) ?? AdminInfo.Empty;
            var travel = (info.DateTo ?? e.DateTo ?? e.DateFrom) >= today ? await TravelAsync(info.City ?? e.City, info.Country ?? e.Country, info, places, ct) : null;

            await store.SetIfChangedAsync($"events/{e.Id}", new
            {
                id = e.Id.ToString(),
                e.Name,
                DateFrom = Iso(e.DateFrom),
                DateTo = Iso(e.DateTo ?? e.DateFrom),
                Year = e.DateFrom!.Value.Year,
                e.City,
                e.Country,
                facts.IsWsdc,
                e.TicketUrl,
                Coords = travel?.Coords is { } c ? new { lat = c.Lat, lng = c.Lng } : null,
                Airports = travel?.Airports.Select(a => new { a.Iata, a.Name }),
                Station = travel?.Station is { } s ? new { s.Slug, s.Name } : null,
                Strengths = facts.Strengths.Select(s => new
                {
                    s.Division,
                    Role = s.Role.ToString(),
                    s.FieldSize,
                    AveragePoints = Math.Round(s.AveragePoints, 2),
                    MedianPoints = Math.Round(s.MedianPoints, 2),
                    TopQuartileAverage = Math.Round(s.TopQuartileAverage, 2),
                    Difficulty = s.Difficulty?.ToString(),
                }),
                StrengthsFrom = facts.StrengthsFrom is { } p ? new { id = p.Id.ToString(), p.Name, DateFrom = Iso(p.DateFrom) } : null,
                facts.Results,
            }, ct);
        }

        foreach (var year in all.GroupBy(f => f.Event.DateFrom!.Value.Year))
        {
            await store.SetIfChangedAsync($"years/{year.Key}", new
            {
                Events = year.Select(f => new
                {
                    id = f.Event.Id.ToString(),
                    f.Event.Name,
                    DateFrom = Iso(f.Event.DateFrom),
                    DateTo = Iso(f.Event.DateTo ?? f.Event.DateFrom),
                    f.Event.City,
                    f.Event.Country,
                    f.IsWsdc,
                    Chips = Chips(f.Strengths),
                }),
            }, ct);
        }

        LogPublished(logger, all.Count, store.Written, store.Skipped);
    }

    public static async Task<IReadOnlyDictionary<string, AdminInfo>> AdminInfosAsync(FirestoreStore store, CancellationToken ct) =>
        (await store.ReadAllAsync("info", ct)).ToDictionary(p => p.Key, p => AdminInfo.From(p.Value));

    /// <summary>
    /// Where the event is and how to get there. The admin's venue and airports win; otherwise the
    /// city is geocoded, and the three nearest airports outside Poland are used. Without a city there
    /// is nothing to go on: the country's middle would pick the wrong airports.
    /// </summary>
    public static async Task<TravelFacts> TravelAsync(string? city, string? country, AdminInfo info, Places places, CancellationToken ct)
    {
        var query = info.VenueAddress ?? (city is null ? null : $"{city}, {country}");

        (double, double)? coords = info is { Latitude: { } la, Longitude: { } lo }
            ? (la, lo)
            : query is null ? null : await places.GeocodeAsync(query, ct);

        if (country is "Poland")
        {
            var station = coords is { } c ? Geo.MainStation(await places.StationsAsync(ct), c.Item1, c.Item2) : null;
            return new TravelFacts(coords, [], station);
        }

        IReadOnlyList<Airport> airports;
        if (info.Airports.Count > 0)
        {
            var known = (await places.AirportsAsync(ct)).ToDictionary(a => a.Iata);
            airports = [.. info.Airports.Select(code => known.GetValueOrDefault(code) ?? new Airport(code, code, "", 0, 0))];
        }
        else
        {
            airports = coords is { } c
                ? Geo.NearestAirports((await places.AirportsAsync(ct)).Where(a => a.CountryCode != "PL"), c.Item1, c.Item2)
                : [];
        }

        return new TravelFacts(coords, airports, null);
    }

    /// <summary>One chip per main-ladder division, leader and follower difficulty averaged.</summary>
    internal static IEnumerable<object> Chips(IReadOnlyList<DivisionStrength> strengths) =>
        strengths
            .Where(s => Strength.DivisionOrder(s.Division) < 10)
            .GroupBy(s => s.Division)
            .Select(g =>
            {
                var known = g.Where(s => s.Difficulty is not null).Select(s => (int)s.Difficulty!.Value).ToList();
                Difficulty? level = known.Count is 0 ? null : (Difficulty)(int)Math.Round(known.Average(), MidpointRounding.AwayFromZero);
                return (object)new { division = g.Key, level = level?.ToString() };
            });

    private static string? Iso(DateOnly? d) => d?.ToString("yyyy-MM-dd");

    [LoggerMessage(Level = LogLevel.Information, Message = "Published {Events} events: {Written} documents written, {Skipped} unchanged")]
    private static partial void LogPublished(ILogger logger, int events, int written, int skipped);
}
