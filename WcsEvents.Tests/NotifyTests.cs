using System.Text.Json;
using WcsEvents.Sync.Publish;

namespace WcsEvents.Tests;

public sealed class NotifyTests
{
    [Fact]
    public void Fares_KeepsEachLegsLowestPriceAcrossDocuments()
    {
        var flights = new Dictionary<string, JsonElement>
        {
            ["x325_WAW-WMI"] = JsonDocument.Parse("""
                { "eventId": "x325", "out": [ { "airline": "Ryanair", "from": "WMI", "to": "BTS", "date": "2026-11-13", "price": 75 } ], "back": [],
                  "combos": [ { "out": { "airline": "Ryanair", "from": "WMI", "to": "BTS", "date": "2026-11-13", "price": 75 },
                                "back": { "airline": "Wizz", "from": "BUD", "to": "WAW", "date": "2026-11-16", "price": 99 } } ] }
                """).RootElement,
            ["x325_KRK"] = JsonDocument.Parse("""
                { "eventId": "x325", "out": [], "back": [ { "airline": "Wizz", "from": "BUD", "to": "WAW", "date": "2026-11-16", "price": 89 } ] }
                """).RootElement,
        };
        var trains = new Dictionary<string, JsonElement>
        {
            ["439_warszawa"] = JsonDocument.Parse("""
                { "eventId": "439", "city": "warszawa", "station": "Kraków Główny",
                  "out": [ { "date": "2026-12-10", "departure": "06:15", "price": 89 } ], "back": [] }
                """).RootElement,
        };

        var fares = NotifyPublisher.Fares(flights, trains);

        Assert.Equal(75, fares[NotifyPublisher.LegKey("x325", "flight", "Ryanair", "WMI", "BTS", "2026-11-13", null)]);
        Assert.Equal(89, fares[NotifyPublisher.LegKey("x325", "flight", "Wizz", "BUD", "WAW", "2026-11-16", null)]);
        Assert.Equal(89, fares[NotifyPublisher.LegKey("439", "train", null, "warszawa", "Kraków Główny", "2026-12-10", "06:15")]);
    }

    [Fact]
    public void Watch_ReadsWhatTheEventPageStores()
    {
        var w = NotifyPublisher.Watch.Read(new Dictionary<string, object>
        {
            ["eventId"] = "x325", ["eventName"] = "Autumn Swing Challenge 2026", ["kind"] = "flight", ["airline"] = "Wizz",
            ["from"] = "BUD", ["to"] = "WAW", ["date"] = "2026-11-16", ["price"] = 120.0, ["currency"] = "PLN",
        });

        Assert.NotNull(w);
        Assert.Equal(NotifyPublisher.LegKey("x325", "flight", "Wizz", "BUD", "WAW", "2026-11-16", null), w.Key);
        var body = NotifyPublisher.Body([new NotifyPublisher.Drop(w, 89)]);
        Assert.Contains("now <strong>89 PLN</strong> (was 120)", body);
        Assert.Contains("<a href=\"https://www.wizzair.com/en-gb/booking/select-flight/BUD/WAW/2026-11-16/null/1/0/0/null\">book</a>", body);
    }
}
