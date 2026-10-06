using WcsEvents.Sync.Travel;

namespace WcsEvents.Tests;

public sealed class TravelTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine("Fixtures", name));

    private static FlightLeg Leg(Airline airline, string from, string to, int day, decimal price, string currency = "PLN") =>
        new(airline, from, to, new DateOnly(2026, 11, day), [], null, null, price, currency);

    [Fact]
    public void Ryanair_ReadsRouteTimesAndPrice()
    {
        var leg = Assert.Single(RyanairClient.Parse(Fixture("ryanair-oneway.json")));

        Assert.Equal(("KRK", "BGY"), (leg.From, leg.To));
        Assert.Equal(new DateOnly(2026, 11, 6), leg.Date);
        Assert.Equal(TimeSpan.FromMinutes(110), leg.Duration);
        Assert.Equal(95.00m, leg.Price);
        Assert.Equal("PLN", leg.Currency);
    }

    [Fact]
    public void Wizz_ReadsOneFarePerDayWithItsDepartures()
    {
        var (outbound, back) = WizzClient.Parse(Fixture("wizz-timetable.json"));

        Assert.NotEmpty(outbound);
        Assert.Empty(back);
        var first = outbound[0];
        Assert.Equal(("KTW", "BCN"), (first.From, first.To));
        Assert.Equal(182.9m, first.Price);
        Assert.Equal([new TimeOnly(5, 30), new TimeOnly(12, 30)], first.Times);
        Assert.Null(first.Duration);
    }

    [Fact]
    public void Wizz_SkipsDaysWithoutAPrice()
    {
        const string body = """{"outboundFlights":[{"departureStation":"KTW","arrivalStation":"BCN","departureDate":"2026-11-05T00:00:00","price":{"amount":0,"currencyCode":"PLN"},"priceType":"checkPrice","departureDates":[]}],"returnFlights":[]}""";

        Assert.Empty(WizzClient.Parse(body).Out);
    }

    [Fact]
    public void Combos_PairAnyOutboundWithAnyReturnCheapestFirst()
    {
        FlightLeg[] outs = [Leg(Airline.Ryanair, "KRK", "BGY", 5, 300), Leg(Airline.Wizz, "KTW", "BGY", 6, 120)];
        FlightLeg[] backs = [Leg(Airline.Ryanair, "BGY", "KRK", 8, 90), Leg(Airline.Wizz, "BGY", "KTW", 9, 200), Leg(Airline.Wizz, "BGY", "KTW", 9, 10, "EUR")];

        var combos = FlightCombos.Cheapest(outs, backs, "PLN", 10);

        Assert.Equal(4, combos.Count);
        Assert.Equal(210m, combos[0].PerPerson);
        Assert.Equal(("KTW", "KRK"), (combos[0].Out.From, combos[0].Back.To));
        Assert.All(combos, c => Assert.Equal("PLN", c.Back.Currency));
    }

    [Fact]
    public void Windows_AreTheDayBeforeTheStartAndTheDayAfterTheEnd()
    {
        Assert.Equal((new DateOnly(2026, 11, 5), new DateOnly(2026, 11, 6)), FlightCombos.OutboundWindow(new DateOnly(2026, 11, 6)));
        Assert.Equal((new DateOnly(2026, 11, 8), new DateOnly(2026, 11, 9)), FlightCombos.ReturnWindow(new DateOnly(2026, 11, 8)));
    }

    [Fact]
    public void NearestAirports_AreWithinReachAndClosestFirst()
    {
        Airport[] airports =
        [
            new("BGY", "Bergamo", "IT", 45.67, 9.70),
            new("LIN", "Linate", "IT", 45.45, 9.28),
            new("MXP", "Malpensa", "IT", 45.63, 8.72),
            new("FCO", "Rome", "IT", 41.80, 12.25),
        ];

        var near = Geo.NearestAirports(airports, 45.46, 9.19);

        Assert.Equal(["LIN", "MXP", "BGY"], near.Select(a => a.Iata));
    }

    [Fact]
    public void MainStation_IsTheBusiestNearbyNotTheClosest()
    {
        Station[] stations =
        [
            new("krakow-zablocie", "Kraków Zabłocie", 50.048, 19.958, 10),
            new("krakow-glowny", "Kraków Główny", 50.067, 19.947, 9530),
            new("warszawa-centralna", "Warszawa Centralna", 52.228, 21.003, 9526),
        ];

        Assert.Equal("krakow-glowny", Geo.MainStation(stations, 50.049, 19.957)?.Slug);
    }
}
