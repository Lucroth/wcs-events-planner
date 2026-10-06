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
}
