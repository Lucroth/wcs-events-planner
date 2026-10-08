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
}
