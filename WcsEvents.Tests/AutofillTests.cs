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

    [Fact]
    public void Missing_CorrectsEveryFieldWhileTheNoteIsThereAndOnlyEmptyOnesOnceTheAdminSaved()
    {
        var suggested = JsonDocument.Parse("""{ "passes": [{ "kind": "Party", "price": 140 }], "venueName": "Hall" }""").RootElement;
        var untouched = JsonDocument.Parse("""{ "passes": [{ "kind": "Full", "price": 190 }], "venueName": "Old hall", "autofill": { "fields": ["venueName"] } }""").RootElement;
        var edited = JsonDocument.Parse("""{ "passes": [{ "kind": "Full", "price": 190 }], "venueName": "My hall" }""").RootElement;

        Assert.Equal(["passes", "venueName"], AutofillPublisher.Missing(suggested, untouched).Keys);
        Assert.Empty(AutofillPublisher.Missing(suggested, edited));
    }

    [Fact]
    public void Resolve_FollowsACalendarEditionToTheIdScoringDanceListsItUnder()
    {
        List<AutofillPublisher.ListedEvent> listed =
        [
            new("457", "Flowstate 2026", 2026, "330"),
            new("x352", "Westie Gala 2026/27", 2026, "352"),
            new("470", "Norway Westie Fest 2027", 2027, null),
        ];

        Assert.Equal("457", AutofillPublisher.Resolve("x330", listed));
        Assert.Equal("x352", AutofillPublisher.Resolve("x352", listed));
        Assert.Equal("x999", AutofillPublisher.Resolve("x999", listed));
        Assert.Equal("470", AutofillPublisher.Resolve("w-norway-westie-fest-2027", listed));
        Assert.Equal("379", AutofillPublisher.Resolve("379", listed));
    }
}
