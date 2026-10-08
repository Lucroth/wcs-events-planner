using WcsEvents.Sync.Tickets;

namespace WcsEvents.Tests;

public sealed class DanceAppTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine("Fixtures", name));

    [Fact]
    public void ParseTickets_ReadsTiersDeadlinesAndTheTierOnSale()
    {
        var (currency, offers) = DanceAppClient.ParseTickets(Fixture("danceapp-register-174.html"))!.Value;

        Assert.Equal("EUR", currency);
        var full = Assert.Single(offers, o => o.Name == "FULL-PASS");
        Assert.Equal([170m, 180m, 190m, 200m, 210m], full.Tiers.Select(t => t.Price));
        Assert.Equal("Late Bird", Assert.Single(full.Tiers, t => t.Current).Name);
        Assert.Equal(new DateOnly(2026, 12, 1), full.Tiers.Single(t => t.Current).Until);
        Assert.Null(full.Tiers[0].Until);
        Assert.True(full.Tiers[0].Past);
        Assert.False(full.SoldOut);

        // The party pass has a waiting list instead of a sale.
        Assert.True(offers.Single(o => o.Name == "PARTY-PASS").SoldOut);
    }

    [Fact]
    public void Passes_KeepsFullAndPartyAndPastTiersOnlyWithADeadline()
    {
        var (currency, offers) = DanceAppClient.ParseTickets(Fixture("danceapp-register-174.html"))!.Value;

        var passes = DanceAppClient.Passes(currency, offers);

        // The first full tier is over and shows no deadline, so it is dropped; the others stay.
        Assert.Equal([180m, 190m, 200m, 210m], passes.Where(p => p.Kind == "Full").Select(p => p.Price));
        Assert.Equal("Late Bird", Assert.Single(passes, p => p is { Kind: "Full", Current: true }).Tier);
        Assert.Equal(["Early-Bird", "Normal Bird", "Late Bird"], passes.Where(p => p.Kind == "Party").Select(p => p.Tier));
        Assert.True(Assert.Single(passes, p => p is { Kind: "Party", Current: true }).SoldOut);
        Assert.DoesNotContain(passes, p => p.Kind == "Full" && p.SoldOut);
    }

    [Fact]
    public void Passes_LeavesOutSpecialPassesAndReadsCurrencyFromTheTicketLink()
    {
        var (currency, offers) = DanceAppClient.ParseTickets(Fixture("danceapp-register-176.html"))!.Value;

        var passes = DanceAppClient.Passes(currency, offers);

        // Extreme, V.I.P. and judges' passes are not the pass a dancer plans a trip around.
        Assert.Equal("SEK", currency);
        Assert.Equal(["Full", "Party"], passes.Select(p => p.Kind).Distinct());
        Assert.Equal(2050m, passes.First(p => p.Kind == "Full").Price);
        Assert.Equal(new DateOnly(2026, 5, 2), passes.First(p => p.Kind == "Full").Until);
        Assert.Equal("Late Bird", Assert.Single(passes, p => p is { Kind: "Full", Current: true }).Tier);
    }

    [Theory]
    [InlineData("€190.00", "190")]
    [InlineData("kr2,050.00", "2050")]
    [InlineData("£125.00", "125")]
    [InlineData("CHF 1,190.50", "1190.5")]
    [InlineData("Free", null)]
    public void ParsePrice_ReadsTheAmountWhateverTheCurrencySign(string text, string? expected) =>
        Assert.Equal(expected is null ? null : decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), DanceAppClient.ParsePrice(text));

    [Theory]
    [InlineData("FULL-PASS", "Full")]
    [InlineData("Party Pass", "Party")]
    [InlineData("Event Pass", "Party")]
    [InlineData("Full Pass - JOKER", null)]
    [InlineData("ALL STAR PASS", null)]
    [InlineData("Extreme Pass", null)]
    [InlineData("Student Pass", null)]
    public void KindOf_NamesOnlyTheTwoPassesAPlanIsBuiltAround(string name, string? kind) =>
        Assert.Equal(kind, DanceAppClient.KindOf(name));

    [Fact]
    public void Match_PairsEventsByNameAndYearAndLeavesAmbiguousOnesAlone()
    {
        var list = DanceAppClient.ParseList(Fixture("danceapp-events.html"));
        var events = new (string Id, string Name, DateOnly From, DateOnly To)[]
        {
            ("x206", "Paris Swing Classic 2027", new DateOnly(2027, 2, 25), new DateOnly(2027, 3, 1)),
            ("x362", "Mystery Swing 2027", new DateOnly(2027, 5, 1), new DateOnly(2027, 5, 3)),
            ("w-franconian-swing-project-2027", "Franconian Swing Project 2027", new DateOnly(2027, 4, 8), new DateOnly(2027, 4, 11)),
        };

        var matches = DanceAppClient.Match(events, list);

        Assert.Equal(174, matches["x206"]);
        Assert.Equal(168, matches["w-franconian-swing-project-2027"]);
        Assert.DoesNotContain("x362", matches.Keys);
    }
}
