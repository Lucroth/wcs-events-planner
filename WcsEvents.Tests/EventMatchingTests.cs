using WcsEvents.Sync.Metrics;

namespace WcsEvents.Tests;

/// <summary>
/// Every pairing here is taken from the real mirror — the registry and scoring.dance names for one
/// actual event, or two different years of one actual annual event.
/// </summary>
public class EventMatchingTests
{
    private static DateOnly D(string iso) => DateOnly.Parse(iso);

    [Theory]
    // The registry prints the full formal title, scoring.dance a short one with the year appended.
    [InlineData("BudaFest Open WCS Championships", "2020-01-01", "Budafest 2020", "2020-01-02")]
    [InlineData("German Open WCS Championships", "2019-08-01", "German Open 2019", "2019-08-08")]
    [InlineData("French Open WCS", "2024-05-01", "French Open 2024", "2024-05-08")]
    [InlineData("Mediterranean Open WCS", "2025-07-01", "Mediterranean Open 2025", "2025-07-11")]
    [InlineData("UK WCS Championships", "2025-03-01", "UK West Coast Swing Championships 2025", "2025-03-14")]
    // Punctuation, spacing and case differ freely between the two sources.
    [InlineData("Boogie By The Bay", "2025-10-01", "Boogie By the Bay", "2025-10-16")]
    [InlineData("D-Townswing", "2023-04-01", "D-Town Swing 2023", "2023-04-21")]
    [InlineData("Neverland Swing", "2019-07-01", "NeverlandSwing 2019", "2019-06-27")]
    // A New Year's event the two sources file under different months.
    [InlineData("Westie Gala", "2025-01-01", "Westie Gala 2024/2025", "2024-12-28")]
    public void SameEvent_MatchesOneEventNamedDifferentlyByEachSource(
        string registryName, string registryDate, string scoringName, string scoringDate) =>
        Assert.True(EventMatching.SameEvent(registryName, D(registryDate), scoringName, D(scoringDate)));

    [Theory]
    // Annual events keep their name, so the name alone cannot tell two years apart — only the date can.
    [InlineData("Boogie By The Bay", "2008-10-01", "Boogie By the Bay", "2025-10-16")]
    [InlineData("Halloween SwingThing", "1994-10-01", "Halloween SwingThing 2024", "2024-10-25")]
    [InlineData("Sea to Sky", "2009-09-01", "Sea to Sky 2024", "2024-11-07")]
    [InlineData("Avignon City Swing", "2023-01-01", "Avignon City Swing 2025", "2025-01-17")]
    [InlineData("German Open", "2017-08-01", "German Open 2022", "2022-08-04")]
    public void SameEvent_KeepsDifferentYearsOfTheSameAnnualApart(
        string registryName, string registryDate, string scoringName, string scoringDate) =>
        Assert.False(EventMatching.SameEvent(registryName, D(registryDate), scoringName, D(scoringDate)));

    [Fact]
    public void SameEvent_AllowsAMonthOfSlackForTheRegistrysFirstOfTheMonthDates()
    {
        Assert.True(EventMatching.SameEvent("Swingtzerland", D("2024-06-01"), "Swingtzerland 2024", D("2024-06-29")));
        Assert.True(EventMatching.SameEvent("Swingtzerland", D("2024-07-01"), "Swingtzerland 2024", D("2024-06-29")));
        Assert.False(EventMatching.SameEvent("Swingtzerland", D("2024-08-01"), "Swingtzerland 2024", D("2024-06-29")));
    }

    [Fact]
    public void SameEvent_NeedsBothDates()
    {
        Assert.False(EventMatching.SameEvent("Swingtzerland", null, "Swingtzerland", D("2024-06-29")));
        Assert.False(EventMatching.SameEvent("Swingtzerland", D("2024-06-01"), "Swingtzerland", null));
    }

    [Fact]
    public void SameName_RequiresAnExactMatchForNamesTooShortToContainMeaningfully()
    {
        // "swing" would otherwise be a substring of half the mirror.
        Assert.False(EventMatching.SameName("Swing", "Swing Fling 2024"));
        Assert.True(EventMatching.SameName("Swing", "swing!"));
    }

    [Fact]
    public void Normalize_StripsOnlyAFourDigitTrailingYear()
    {
        Assert.Equal("budafest", EventMatching.Normalize("BudaFest 2024"));
        // The 5280 is the name, not a year — a leading run is never a suffix to strip.
        Assert.Equal("5280swingdancechampionships", EventMatching.Normalize("5280 Swing Dance Championships"));
        Assert.Equal("asiawcsopenxiii", EventMatching.Normalize("Asia WCS Open XIII"));
        Assert.Equal(string.Empty, EventMatching.Normalize("   "));
        Assert.Equal(string.Empty, EventMatching.Normalize(null));
    }

    [Theory]
    [InlineData("SwingVester 2026/27 WSDC", "SwingVester 2025/26 WSDC")]
    [InlineData("SwingVester 2026/27 WSDC", "SwingVester 2024/25")]
    [InlineData("River Swing Nights - 2026 - WSDC Trial Event", "River Swing Nights 2025")]
    [InlineData("King Swing 2026PL", "King Swing 2025")]
    [InlineData("Westie Gala 2024/2025", "Westie Gala")]
    public void SameName_IgnoresSeasonYearsAndEditionTags(string a, string b) =>
        Assert.True(EventMatching.SameName(a, b));
}
