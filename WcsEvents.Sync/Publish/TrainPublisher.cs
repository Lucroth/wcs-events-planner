using WcsEvents.Sync.Data;
using WcsEvents.Sync.Travel;

namespace WcsEvents.Sync.Publish;

/// <summary>
/// Train fares from every Polish home city to every Polish event in the next few weeks, written to
/// <c>trains/{eventId}_{citySlug}</c>. Out the day before or of the start, back the day of or after
/// the end, like flights. Events further ahead are skipped: PKP Intercity does not sell them yet.
/// </summary>
public sealed partial class TrainPublisher(
    AppDbContext db, Places places, KoleoClient koleo, FirestoreStore store, TimeProvider time, ILogger<TrainPublisher> logger)
{
    private const int HorizonDays = 35;

    public async Task PublishAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var trips = await Trip.LoadAsync(db, store, ct);

        var upcoming = trips.Where(t => t.Country is "Poland" && t.Start > today && t.Start <= today.AddDays(HorizonDays)).ToList();
        var searched = 0;

        foreach (var trip in upcoming)
        {
            var travel = await EventPublisher.TravelAsync(trip.City, trip.Country, trip.Info, places, ct);
            if (travel.Station is not { } station)
            {
                continue;
            }

            foreach (var city in HomeCities.All.DistinctBy(c => c.KoleoSlug))
            {
                if (SameCity(station, city))
                {
                    continue;
                }

                List<TrainLeg> outLegs = [];
                List<TrainLeg> backLegs = [];
                foreach (var day in (DateOnly[])[trip.Start.AddDays(-1), trip.Start])
                {
                    outLegs.AddRange(await koleo.DayAsync(city.KoleoSlug, station.Slug, day, ct));
                }

                foreach (var day in (DateOnly[])[trip.End, trip.End.AddDays(1)])
                {
                    backLegs.AddRange(await koleo.DayAsync(station.Slug, city.KoleoSlug, day, ct));
                }

                searched++;
                var cheapestOut = outLegs.MinBy(l => l.Price);
                var cheapestBack = backLegs.MinBy(l => l.Price);

                await store.SetIfChangedAsync($"trains/{trip.Id}_{city.KoleoSlug}", new
                {
                    EventId = trip.Id,
                    City = city.KoleoSlug,
                    Station = station.Name,
                    Currency = "PLN",
                    Cheapest = cheapestOut is not null && cheapestBack is not null ? cheapestOut.Price + cheapestBack.Price : (decimal?)null,
                    Out = outLegs.OrderBy(l => l.Departure).Select(Leg),
                    Back = backLegs.OrderBy(l => l.Departure).Select(Leg),
                    FetchedOn = today.ToString("yyyy-MM-dd"),
                }, ct);
            }
        }

        LogPublished(logger, upcoming.Count, searched, store.Written, store.Skipped);
    }

    /// <summary>Station names start with their city's ("Warszawa Centralna"); nobody takes a train home to home.</summary>
    internal static bool SameCity(Station station, HomeCity city) =>
        station.Name.StartsWith(city.Name, StringComparison.OrdinalIgnoreCase);

    private static object Leg(TrainLeg l) => new
    {
        Date = l.Departure.ToString("yyyy-MM-dd"),
        Departure = l.Departure.ToString("HH:mm"),
        Arrival = l.Arrival.ToString("HH:mm"),
        DurationMinutes = (int)(l.Arrival - l.Departure).TotalMinutes,
        l.Changes,
        l.Price,
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Trains for {Events} upcoming Polish events: {Searches} searches, {Written} documents written, {Skipped} unchanged")]
    private static partial void LogPublished(ILogger logger, int events, int searches, int written, int skipped);
}
