using WcsEvents.Sync.Calendar;
using WcsEvents.Sync.Data;

namespace WcsEvents.Tests;

public sealed class CalendarTests
{
    private static readonly DateOnly Today = new(2026, 10, 8);

    private static CalendarEvent Entry(string name, string from, string to, string country = "Germany", string? city = null, string? address = null) =>
        new(name, DateOnly.Parse(from), DateOnly.Parse(to), null, city, country, address);

    private static ScoringEvent Scored(int id, string name, string from, string country = "Germany", string? city = null) =>
        new() { Id = id, Name = name, DateFrom = DateOnly.Parse(from), Country = country, City = city };

    [Fact]
    public void Parse_ReadsDatesAddressAndCountryFromTheStructuredData()
    {
        var events = WsdcCalendar.Parse(File.ReadAllText(Path.Combine("Fixtures", "wsdc-calendar.html")));

        var paris = Assert.Single(events, e => e.Name == "Paris Swing Classic");
        Assert.Equal((new DateOnly(2027, 2, 25), new DateOnly(2027, 3, 1)), (paris.From, paris.To));
        Assert.Equal("France", paris.Country);
        Assert.Equal("Paris", paris.City);
        Assert.Equal("149 Bd Anatole France, 93200 Saint-Denis, France, Paris", paris.Address);
        Assert.Equal("https://parisswingclassic.com/", paris.WebsiteUrl);

        // The event on hiatus is on the calendar but not happening; an empty country is read off the street.
        Assert.DoesNotContain(events, e => e.Name.Contains("Hiatus"));
        Assert.Null(Assert.Single(events, e => e.Name == "Euro Dance Festival").Country);
        Assert.Equal("Russia", Assert.Single(events, e => e.Name.StartsWith("Moscow")).Country);
    }

    [Theory]
    [InlineData("http://Https://parisswingclassic.com", "https://parisswingclassic.com/")]
    [InlineData("example.org/swing", "https://example.org/swing")]
    [InlineData("  ", null)]
    public void CleanUrl_FixesWhatTheWsdcTyped(string raw, string? expected) =>
        Assert.Equal(expected, WsdcCalendar.CleanUrl(raw));

    [Fact]
    public void Build_MatchesTheListedEditionAndAddsTheRest()
    {
        List<ScoringEvent> known =
        [
            Scored(379, "WCS Festival Düsseldorf 2026", "2026-10-15"),
            Scored(433, "Scandinavian Open 2026 [SNOW]", "2026-10-28", "Sweden"),
            Scored(206, "Paris Swing Classic 2025", "2025-01-30", "France"),
            Scored(54, "Swing Fling 2025", "2025-05-02", "United Kingdom"),
        ];

        var plan = CalendarPlan.Build(
            [
                Entry("WCS Festival", "2026-10-16", "2026-10-19"),
                Entry("Scandinavian Open WCS [SNOW ]", "2026-10-28", "2026-11-02", "Sweden"),
                Entry("Paris Swing Classic", "2027-02-25", "2027-03-01", "France"),
                Entry("Swing In Paris", "2027-01-22", "2027-01-25", "France"),
                Entry("Swing Fling", "2027-05-01", "2027-05-03", "United States of America"),
                Entry("Old Event", "2026-09-01", "2026-09-03"),
            ],
            known,
            Today);

        // The calendar's dates win for the editions scoring.dance lists, whatever the spelling of the name.
        Assert.Equal([379, 433], plan.Matched.Keys.Order());
        Assert.Equal(new DateOnly(2026, 10, 16), plan.Matched[379].From);

        // Paris continues scoring.dance's series; the rest are new. A same-named event in another
        // country is not the series, and a finished one is ignored.
        Assert.Equal(
            ["w-swing-fling-2027", "w-swing-in-paris-2027", "x206"],
            plan.Added.Select(p => p.Id).Order());
        var paris = plan.Added.Single(p => p.Id == "x206");
        Assert.Equal("Paris Swing Classic 2027", paris.Name);
        Assert.Equal(206, paris.Latest!.Id);
    }

    [Fact]
    public void European_KeepsAnEditionWithNoCountryWhenItsSeriesIsKnownInEurope()
    {
        CalendarEvent noCountry = new("Euro Dance Festival", new DateOnly(2029, 2, 13), new DateOnly(2029, 2, 18), null, "Rust", null, null);
        CalendarEvent unknownSeries = noCountry with { Name = "Mystery Fest" };
        CalendarEvent american = noCountry with { Name = "Boogie By The Bay", Country = "United States of America" };

        var kept = CalendarPlan.European([noCountry, unknownSeries, american], [Scored(335, "Euro Dance Festival 2026", "2026-02-17")]);

        Assert.Equal([noCountry], kept);
    }

    [Fact]
    public void Build_GivesASeriesLaterEditionsAYearSuffix()
    {
        var plan = CalendarPlan.Build(
            [
                Entry("Euro Dance Festival", "2027-02-09", "2027-02-14"),
                Entry("Euro Dance Festival", "2028-02-29", "2028-03-05"),
                Entry("Westie Gala", "2026-12-31", "2027-01-04", "Sweden"),
            ],
            [Scored(335, "Euro Dance Festival 2026", "2026-02-17"), Scored(352, "Westie Gala 2025/2026", "2025-12-28", "Sweden")],
            Today);

        Assert.Equal(["x335", "x335-2028", "x352"], plan.Added.Select(p => p.Id).Order());
        Assert.Equal("Westie Gala 2026/27", plan.Added.Single(p => p.Id == "x352").Name);
    }

    [Fact]
    public void Apply_TakesTheCalendarsDatesAndFillsWhatIsMissing()
    {
        var swingvester = Scored(350, "SwingVester 2026/27 WSDC", "2026-12-31", "Austria");
        var plan = CalendarPlan.Build(
            [new CalendarEvent("SwingVester", new DateOnly(2026, 12, 30), new DateOnly(2027, 1, 4), "https://swingvester.com/", "Wels", "Austria", null)],
            [swingvester],
            Today);

        Assert.True(plan.Apply(swingvester));
        Assert.Equal((new DateOnly(2026, 12, 30), new DateOnly(2027, 1, 4), "Wels", "https://swingvester.com/"), (swingvester.DateFrom!.Value, swingvester.DateTo!.Value, swingvester.City, swingvester.TicketUrl));
        Assert.False(plan.Apply(Scored(1, "Other", "2026-11-01")));
    }

    [Theory]
    [InlineData("Düsseldorf", "Duesseldorf", true)]
    [InlineData("Kraków", "Krakow", true)]
    [InlineData("Warszawa", "Warsaw", true)]
    [InlineData("Lyon", "Dadrilly", false)]
    public void SameCity_IgnoresSpellingNotPlace(string a, string b, bool same) =>
        Assert.Equal(same, CalendarPlan.SameCity(a, b));
}
