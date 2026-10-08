using System.Text.Json;
using WcsEvents.Sync.Publish;

namespace WcsEvents.Tests;

public sealed class AutofillTests
{
    [Fact]
    public void Missing_FillsOnlyFieldsTheAdminLeftEmpty()
    {
        var suggested = JsonDocument.Parse("""
            { "websiteUrl": "https://site", "venueName": "Hall", "staff": ["A & B"], "override": { "city": "Cologne" }, "passes": [{ "kind": "Full", "price": 200 }] }
            """).RootElement;
        var current = JsonDocument.Parse("""
            { "websiteUrl": "https://admin", "venueName": "  ", "staff": [], "override": { "city": "" }, "passes": [{ "kind": "Full", "price": 180 }] }
            """).RootElement;

        var fields = AutofillPublisher.Missing(suggested, current);

        Assert.Equal(["venueName", "staff", "override"], fields.Keys);
        Assert.Equal(5, AutofillPublisher.Missing(suggested, null).Count);
    }

    [Fact]
    public void Missing_AddsOnlyTheMissingPartsOfAnOverrideTheAdminStarted()
    {
        var suggested = JsonDocument.Parse("""{ "override": { "dateFrom": "2027-10-08", "dateTo": "2027-10-10", "city": "Hamar" } }""").RootElement;
        var current = JsonDocument.Parse("""{ "override": { "city": "Stavanger" } }""").RootElement;

        var fields = AutofillPublisher.Missing(suggested, current);

        var dates = Assert.IsType<Dictionary<string, object?>>(fields["override"]);
        Assert.Equal(["dateFrom", "dateTo"], dates.Keys);
    }
}
