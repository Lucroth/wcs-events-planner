using WcsEvents.Sync.Calendar;
using WcsEvents.Sync.Data;
using WcsEvents.Sync.Metrics;
using WcsEvents.Sync.Scoring;
using WcsEvents.Sync.Travel;

namespace WcsEvents.Sync.Publish;

public sealed record TravelFacts((double Lat, double Lng)? Coords, IReadOnlyList<Airport> Airports, Station? Station);

/// <summary>
/// Publishes every event to <c>events/{id}</c> and one summary per year to <c>years/{year}</c>, the
/// single document the event list reads. Travel facts (where the event is, which airports and which
/// station serve it) are worked out only for events still to come.
/// </summary>
public sealed partial class EventPublisher(
    EventCatalog catalog, CalendarPlanner planner, Places places, FirestoreStore store, TimeProvider time, ILogger<EventPublisher> logger)
{
    public async Task PublishAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var infos = await AdminInfosAsync(store, ct);
        List<EventFacts> everything = [.. await catalog.AllAsync(ct)];

        // The WSDC calendar is the authority on what is sanctioned and when: an event it lists takes
        // its dates, and counts as WSDC whatever scoring.dance says. Null when it could not be read;
        // everything below then runs on scoring.dance alone and publishes nothing the calendar added.
        var plan = await planner.PlanAsync([.. everything.Select(f => f.Event)], ct);
        for (var i = 0; plan is not null && i < everything.Count; i++)
        {
            if (plan.Apply(everything[i].Event))
            {
                everything[i] = everything[i] with { IsWsdc = true };
            }
        }

        // The app covers European events only. One that is not (or has no country, unless the admin
        // gave it one) is never published, and anything published for it before is taken down.
        List<EventFacts> all = [];
        var removed = 0;
        foreach (var facts in everything)
        {
            var info = infos.GetValueOrDefault(facts.Event.Id.ToString()) ?? AdminInfo.Empty;
            if (Countries.IsEuropean(info.Country ?? facts.Event.Country))
            {
                all.Add(facts);
                continue;
            }

            removed += await store.DeletePublishedAsync($"events/{facts.Event.Id}", isPrefix: false, ct);
            removed += await store.DeletePublishedAsync($"flights/{facts.Event.Id}_", isPrefix: true, ct);
        }

        // Editions the calendar lists that scoring.dance does not are events of their own, with the
        // strengths of the series' latest edition. A series whose next edition is listed needs no guess.
        IReadOnlyList<PlannedEvent> planned = plan?.Added ?? [];
        var factsOf = all.ToDictionary(f => f.Event.Id);
        var nextOf = planned.Where(p => p.Latest is not null && p.Id == $"x{p.Latest.Id}").ToDictionary(p => p.Latest!.Id);
        HashSet<int> covered = [.. nextOf.Values.Where(p => p.Entry.From < p.Latest!.DateFrom!.Value.AddMonths(18)).Select(p => p.Latest!.Id)];
        List<Row> expected = [.. ExpectedEditions(all, today).Where(f => !covered.Contains(f.Event.Id)).Select(Row.Expected)];

        foreach (var facts in all)
        {
            var e = facts.Event;
            var info = infos.GetValueOrDefault(e.Id.ToString()) ?? AdminInfo.Empty;
            var address = plan?.Matched.GetValueOrDefault(e.Id)?.Address;
            var travel = (info.DateTo ?? e.DateTo ?? e.DateFrom) >= today ? await TravelAsync(info.City ?? e.City, info.Country ?? e.Country, info, places, ct, address) : null;

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
                VenueAddress = address,
                Coords = travel?.Coords is { } c ? new { lat = c.Lat, lng = c.Lng } : null,
                Airports = travel?.Airports.Select(a => new { a.Iata, a.Name }),
                Station = travel?.Station is { } s ? new { s.Slug, s.Name } : null,
                Strengths = StrengthDocs(facts.Strengths),
                StrengthsFrom = facts.StrengthsFrom is { } p ? new { id = p.Id.ToString(), p.Name, DateFrom = Iso(p.DateFrom) } : null,
                Previous = Edition(Editions(e, all).LastOrDefault(o => o.DateFrom < e.DateFrom)),
                Next = nextOf.TryGetValue(e.Id, out var next)
                    ? new { id = next.Id, name = next.Name, dateFrom = Iso(next.Entry.From) }
                    : Edition(Editions(e, all).FirstOrDefault(o => o.DateFrom > e.DateFrom)),
                facts.Results,
            }, ct);
        }

        // Where each planned edition is. The address decides the travel; the name shown is the
        // series' usual city when the venue is in the same area, else the calendar's.
        Dictionary<string, (TravelFacts? Travel, string? City)> where = [];
        foreach (var p in planned)
        {
            var info = infos.GetValueOrDefault(p.Id) ?? AdminInfo.Empty;
            var travel = (info.DateTo ?? p.Entry.To) >= today ? await TravelAsync(info.City ?? p.Entry.City, info.Country ?? p.Country, info, places, ct, p.Address) : null;
            where[p.Id] = (travel, await ShownCityAsync(p, travel?.Coords, ct));
        }

        foreach (var p in planned)
        {
            var (travel, city) = where[p.Id];
            var latest = p.Latest is { } l ? factsOf.GetValueOrDefault(l.Id) : null;

            await store.SetIfChangedAsync($"events/{p.Id}", new
            {
                id = p.Id,
                p.Name,
                DateFrom = Iso(p.Entry.From),
                DateTo = Iso(p.Entry.To),
                Year = p.Entry.From.Year,
                City = city,
                p.Country,
                IsWsdc = true,
                TicketUrl = p.Entry.WebsiteUrl,
                VenueAddress = p.Address,
                Coords = travel?.Coords is { } c ? new { lat = c.Lat, lng = c.Lng } : null,
                Airports = travel?.Airports.Select(a => new { a.Iata, a.Name }),
                Station = travel?.Station is { } s ? new { s.Slug, s.Name } : null,
                Strengths = StrengthDocs(latest?.Strengths ?? []),
                StrengthsFrom = latest is { Strengths.Count: > 0 }
                    ? latest.StrengthsFrom is { } from
                        ? new { id = from.Id.ToString(), from.Name, DateFrom = Iso(from.DateFrom) }
                        : new { id = latest.Event.Id.ToString(), latest.Event.Name, DateFrom = Iso(latest.Event.DateFrom) }
                    : null,
                Previous = p.Latest is null ? null : Edition(p.Latest),
                Next = (object?)null,
                Results = Array.Empty<object>(),
                Announced = new { Venue = (string?)null, Source = WsdcCalendar.PageUrl },
            }, ct);
        }

        // A planned event whose edition scoring.dance now lists (or the calendar dropped) goes, with
        // its fares. Only when the calendar was read: a failed fetch must not wipe what it added.
        if (plan is not null)
        {
            HashSet<string> current = [.. planned.Select(p => p.Id)];
            foreach (var path in (await store.PublishedPathsAsync("events/x", ct)).Concat(await store.PublishedPathsAsync("events/w-", ct)))
            {
                var id = path["events/".Length..];
                if (!current.Contains(id))
                {
                    removed += await store.DeletePublishedAsync(path, isPrefix: false, ct);
                    removed += await store.DeletePublishedAsync($"flights/{id}_", isPrefix: true, ct);
                    removed += await store.DeletePublishedAsync($"trains/{id}_", isPrefix: true, ct);
                }
            }
        }

        var rows = all.Select(Row.Listed).Concat(expected).Concat(planned.Select(p => Row.Planned(p, where[p.Id].City, p.Latest is { } l ? factsOf.GetValueOrDefault(l.Id) : null)));

        foreach (var year in rows.GroupBy(r => r.From.Year))
        {
            await store.SetIfChangedAsync($"years/{year.Key}", new
            {
                Events = year.OrderBy(r => r.From).Select(r => new
                {
                    id = r.Id,
                    r.Name,
                    DateFrom = Iso(r.From),
                    DateTo = Iso(r.To),
                    r.City,
                    r.Country,
                    r.IsWsdc,
                    Chips = Chips(r.Strengths),
                    Expected = r.IsExpected ? true : (bool?)null,
                    Announced = r.WebsiteUrl is not null || r.IsAnnounced ? new { Venue = (string?)null, WebsiteUrl = r.WebsiteUrl, Source = WsdcCalendar.PageUrl } : null,
                }),
            }, ct);
        }

        LogPublished(logger, all.Count + planned.Count, store.Written, store.Skipped, removed);
    }

    /// <summary>
    /// The city to show for a planned edition. The calendar names the venue's own town, often a suburb
    /// ("Sipson" for London, "Dadrilly" for Lyon), so the latest edition's city stays when the venue is
    /// within 60 km of it; a venue further away is a real move (Westie Pink City left La Grande-Motte).
    /// </summary>
    private async Task<string?> ShownCityAsync(PlannedEvent p, (double Lat, double Lng)? venue, CancellationToken ct)
    {
        var known = p.Latest?.City;
        var listed = p.Entry.City;
        if (known is null || listed is null)
        {
            return listed ?? known;
        }

        if (CalendarPlan.SameCity(known, listed))
        {
            return known;
        }

        var there = await places.GeocodeAsync($"{known}, {p.Latest!.Country}", ct);
        return venue is { } v && there is { } t && Geo.Km(v.Lat, v.Lng, t.Lat, t.Lng) < 60 ? known : listed;
    }

    public static async Task<IReadOnlyDictionary<string, AdminInfo>> AdminInfosAsync(FirestoreStore store, CancellationToken ct) =>
        (await store.ReadAllAsync("info", ct)).ToDictionary(p => p.Key, p => AdminInfo.From(p.Value));

    /// <summary>
    /// Where the event is and how to get there. The admin's venue and airports win; otherwise the
    /// city is geocoded, and the three nearest airports outside Poland are used. Without a city there
    /// is nothing to go on: the country's middle would pick the wrong airports.
    /// </summary>
    public static async Task<TravelFacts> TravelAsync(string? city, string? country, AdminInfo info, Places places, CancellationToken ct, string? address = null)
    {
        // The admin's coordinates, else the admin's venue address, else the WSDC's, else the city.
        (double, double)? coords = info is { Latitude: { } la, Longitude: { } lo } ? (la, lo) : null;
        foreach (var query in new[] { info.VenueAddress, address, city is null ? null : $"{city}, {country}" })
        {
            if (coords is null && query is not null)
            {
                coords = await places.GeocodeAsync(query, ct);
            }
        }

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

    /// <summary>One chip per main-ladder division, leader and follower difficulty and top-quartile points
    /// averaged, and each role's own for a reader who picks one.</summary>
    internal static IEnumerable<object> Chips(IReadOnlyList<DivisionStrength> strengths) =>
        strengths
            .Where(s => Strength.DivisionOrder(s.Division) < 10)
            .GroupBy(s => s.Division)
            .Select(g =>
            {
                var known = g.Where(s => s.Difficulty is not null).Select(s => (int)s.Difficulty!.Value).ToList();
                Difficulty? level = known.Count is 0 ? null : (Difficulty)(int)Math.Round(known.Average(), MidpointRounding.AwayFromZero);
                return (object)new
                {
                    division = g.Key,
                    level = level?.ToString(),
                    top = Math.Round(g.Average(s => s.TopQuartileAverage), 1),
                    leader = Side(g, Role.Leader),
                    follower = Side(g, Role.Follower),
                };
            });

    private static object? Side(IEnumerable<DivisionStrength> division, Role role) =>
        division.FirstOrDefault(s => s.Role == role) is { } s
            ? new { level = s.Difficulty?.ToString(), top = Math.Round(s.TopQuartileAverage, 1) }
            : null;

    /// <summary>
    /// The latest edition of each series whose anniversary is still ahead and that has no later
    /// edition listed: the series is expected back a year on, on roughly the same dates.
    /// </summary>
    internal static IEnumerable<EventFacts> ExpectedEditions(IReadOnlyList<EventFacts> all, DateOnly today) =>
        all.Where(f => f.Event.DateFrom is { } d
            && d.AddYears(1) >= today
            && !Editions(f.Event, all).Any(o => o.DateFrom > d));

    private static IEnumerable<object> StrengthDocs(IEnumerable<DivisionStrength> strengths) =>
        strengths.Select(s => new
        {
            s.Division,
            Role = s.Role.ToString(),
            s.FieldSize,
            AveragePoints = Math.Round(s.AveragePoints, 2),
            MedianPoints = Math.Round(s.MedianPoints, 2),
            TopQuartileAverage = Math.Round(s.TopQuartileAverage, 2),
            EuropeTopQuartileAverage = Math.Round(s.EuropeTopQuartileAverage, 2),
            Difficulty = s.Difficulty?.ToString(),
        });

    /// <summary>One line of a year summary: a listed event, a series' guessed next edition, or an edition only the WSDC calendar lists.</summary>
    internal sealed record Row(
        string Id, string Name, DateOnly From, DateOnly To, string? City, string? Country, bool IsWsdc,
        IReadOnlyList<DivisionStrength> Strengths, bool IsExpected, bool IsAnnounced, string? WebsiteUrl)
    {
        public static Row Listed(EventFacts f) =>
            new(f.Event.Id.ToString(), f.Event.Name, f.Event.DateFrom!.Value, f.Event.DateTo ?? f.Event.DateFrom!.Value, f.Event.City, f.Event.Country, f.IsWsdc, f.Strengths, false, false, null);

        /// <summary>A year on from the latest edition, on roughly the same dates; it opens that edition's page.</summary>
        public static Row Expected(EventFacts f)
        {
            var from = f.Event.DateFrom!.Value;
            return new(f.Event.Id.ToString(), f.Event.Name, from.AddYears(1), (f.Event.DateTo ?? from).AddYears(1), f.Event.City, f.Event.Country, f.IsWsdc, f.Strengths, true, false, null);
        }

        public static Row Planned(PlannedEvent p, string? city, EventFacts? latest) =>
            new(p.Id, p.Name, p.Entry.From, p.Entry.To, city, p.Country, true, latest?.Strengths ?? [], false, true, p.Entry.WebsiteUrl);
    }

    /// <summary>The other published editions of an event's series, oldest first.</summary>
    internal static IEnumerable<ScoringEvent> Editions(ScoringEvent e, IEnumerable<EventFacts> all) =>
        all.Select(f => f.Event)
            .Where(o => o.Id != e.Id && EventMatching.SameName(o.Name, e.Name))
            .OrderBy(o => o.DateFrom);

    private static object? Edition(ScoringEvent? e) =>
        e is null ? null : new { id = e.Id.ToString(), e.Name, DateFrom = Iso(e.DateFrom) };

    private static string? Iso(DateOnly? d) => d?.ToString("yyyy-MM-dd");

    [LoggerMessage(Level = LogLevel.Information, Message = "Published {Events} European events: {Written} documents written, {Skipped} unchanged, {Removed} outside Europe removed")]
    private static partial void LogPublished(ILogger logger, int events, int written, int skipped, int removed);
}
