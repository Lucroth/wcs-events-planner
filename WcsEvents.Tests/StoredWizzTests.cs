using System.Text.Json;
using WcsEvents.Sync.Publish;
using WcsEvents.Sync.Travel;

namespace WcsEvents.Tests;

public sealed class StoredWizzTests
{
    [Fact]
    public void StoredWizz_ReadsWizzLegsOnceFromListsAndCombos()
    {
        var doc = JsonDocument.Parse("""
            {
              "out": [
                { "airline": "Wizz", "from": "KTW", "to": "BUD", "date": "2026-11-12", "times": ["06:00", "18:30"], "arrival": null, "durationMinutes": null, "price": 109, "currency": "PLN" },
                { "airline": "Ryanair", "from": "WMI", "to": "BTS", "date": "2026-11-13", "times": ["22:40"], "arrival": "00:05", "durationMinutes": 85, "price": 60, "currency": "PLN" }
              ],
              "back": [],
              "combos": [ {
                "out": { "airline": "Wizz", "from": "KTW", "to": "BUD", "date": "2026-11-12", "times": ["06:00", "18:30"], "price": 109, "currency": "PLN" },
                "back": { "airline": "Wizz", "from": "BUD", "to": "KTW", "date": "2026-11-16", "times": ["21:00"], "price": 89, "currency": "PLN" },
                "perPerson": 198 } ]
            }
            """).RootElement;

        var legs = FlightPublisher.StoredWizz(doc);

        Assert.Equal(2, legs.Count);
        Assert.All(legs, l => Assert.Equal(Airline.Wizz, l.Airline));
        Assert.Equal([new TimeOnly(6, 0), new TimeOnly(18, 30)], legs[0].Times);
        Assert.Empty(FlightPublisher.StoredWizz(null));
    }

    [Fact]
    public void History_AddsAPointOnlyWhenTheFareChangesAndKeepsMissingLegsUntilTheirDay()
    {
        var previous = JsonDocument.Parse("""
            { "history": {
                "Wizz|KTW|BUD|2026-11-12": [{ "on": "2026-10-01", "price": 109 }],
                "Ryanair|WMI|BTS|2026-11-13": [{ "on": "2026-10-01", "price": 60 }],
                "Ryanair|WMI|BTS|2026-10-02": [{ "on": "2026-09-20", "price": 40 }] } }
            """).RootElement;
        static FlightLeg Leg(Airline a, string from, string to, string date, decimal price) =>
            new(a, from, to, DateOnly.Parse(date), [], null, null, price, "EUR");

        var history = FlightPublisher.History(previous, [
            Leg(Airline.Wizz, "KTW", "BUD", "2026-11-12", 109),
            Leg(Airline.Wizz, "KTW", "BUD", "2026-11-12", 99),
            Leg(Airline.Wizz, "BUD", "KTW", "2026-11-16", 89),
        ], new DateOnly(2026, 10, 29));

        Assert.Equal([109m, 99m], history["Wizz|KTW|BUD|2026-11-12"].Select(p => p.Price));
        Assert.Equal("2026-10-29", history["Wizz|KTW|BUD|2026-11-12"][^1].On);
        Assert.Single(history["Wizz|BUD|KTW|2026-11-16"]);
        Assert.Single(history["Ryanair|WMI|BTS|2026-11-13"]);
        Assert.False(history.ContainsKey("Ryanair|WMI|BTS|2026-10-02"));
    }
}
