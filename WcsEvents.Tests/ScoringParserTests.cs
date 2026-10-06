using WcsEvents.Sync.Data;
using WcsEvents.Sync.Scoring;

namespace WcsEvents.Tests;

public class ScoringParserTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine("Fixtures", name));

    [Fact]
    public void ParseRoundLinks_FindsEveryRoundOfTheEvent()
    {
        var links = ScoringParser.ParseRoundLinks(Fixture("scoring-event.html"), scoringEventId: 201);

        Assert.Equal(29, links.Count);
        Assert.Contains(links, l => l is { RoundId: 3343, Name: "Novice Jack&Jill prelim" });
        Assert.Contains(links, l => l is { RoundId: 3346, Name: "Novice Jack&Jill final" });
        Assert.Equal(links.Select(l => l.RoundId).Distinct().Count(), links.Count);
    }

    [Fact]
    public void ParseWallRounds_FindsEveryRoundButtonOnTheMarshallingWall()
    {
        var links = ScoringParser.ParseWallRounds(Fixture("scoring-wall.html"));

        Assert.Equal(4, links.Count);
        Assert.Contains(links, l => l is { RoundId: 9001, Name: "Newcomer Jack&Jill prelim" });
        Assert.Contains(links, l => l is { RoundId: 9020, Name: "Strictly Novice prelim" });
    }

    [Fact]
    public void ParseWallHeats_ReadsEveryHeatWithNoResultOfAnyKind()
    {
        var entries = ScoringParser.ParseWallHeats(Fixture("scoring-wall-round.html"));

        // 2 heats x 2 rows, minus the one blank leader cell in heat 2's second row.
        Assert.Equal(7, entries.Count);

        var alex = entries.Single(e => e.Name == "Alex Mercer");
        Assert.Equal(900001, alex.Wscid);
        Assert.Equal("#101", alex.Bib);
        Assert.Equal(Role.Leader, alex.Role);
        Assert.Equal(1, alex.HeatNumber);
        Assert.Equal(1, alex.Position);

        // Not every dancer is registry-linked — the wall still lists them by name alone.
        var sam = entries.Single(e => e.Name == "Sam Whitfield");
        Assert.Null(sam.Wscid);
        Assert.Equal(Role.Leader, sam.Role);

        // An uneven heat leaves one side's bib/name cells blank; that side is skipped entirely
        // rather than yielded as an empty entry — only the follower shows up for that row.
        Assert.DoesNotContain(entries, e => e.HeatNumber == 2 && e.Position == 2 && e.Role == Role.Leader);
        Assert.Contains(entries, e => e is { Name: "Taylor Quinn", Role: Role.Follower, HeatNumber: 2, Position: 2 });
    }

    [Fact]
    public void ParseWallHeats_ReadsAFinalWithNoHeatHeaderAtAll()
    {
        // A final has exactly one group dancing — no "Heat N of M" header the way a prelim's multiple
        // heats need one. Missing the header used to mean the whole card was skipped, silently
        // dropping every final's entries; it must now default to heat 1 instead.
        var entries = ScoringParser.ParseWallHeats(Fixture("scoring-wall-final.html"));

        Assert.Contains(entries, e => e is { Name: "Devon Ashcroft", Role: Role.Leader, HeatNumber: 1, Position: 1, Wscid: 900010 });
        Assert.Contains(entries, e => e is { Name: "Reese Calloway", Role: Role.Follower, HeatNumber: 1, Position: 1, Wscid: 900011 });
        Assert.Contains(entries, e => e is { Name: "Marlowe Finch", Role: Role.Leader, HeatNumber: 1, Position: 4, Wscid: 900012 });

        // The blank spacer row between the field and the alternates contributes nothing.
        Assert.DoesNotContain(entries, e => e.Bib is null && e.Name == string.Empty);

        // An alternate ("ALT1" in the Pos column, not a number) isn't dancing unless a scratch calls
        // them up — excluded entirely, not included with a placeholder position.
        Assert.DoesNotContain(entries, e => e.Name == "Wren Delacroix");
    }

    [Fact]
    public void ParseRound_ReadsTheWholePrelimFieldIncludingUnregisteredDancers()
    {
        var round = ScoringParser.ParseRound(Fixture("scoring-prelim.html"))!;

        Assert.Equal(3343, round.RoundId);
        Assert.Equal("Novice Jack&Jill prelim", round.RoundName);
        Assert.Equal("UK West Coast Swing Championships 2025", round.EventName);
        Assert.Equal(new DateOnly(2025, 3, 27), round.EventDate);
        Assert.Equal("London", round.Location);

        // This is the point of the whole exercise: the registry shows finalists, this shows 202 entrants.
        Assert.Equal(202, round.Entries.Count);
        Assert.Equal(2, round.Entries.Select(e => e.TableIndex).Distinct().Count());

        // Dancers without a registry link still count towards the field.
        Assert.Equal(145, round.Entries.Count(e => e.Wscid is not null));
        Assert.Contains(round.Entries, e => e is { Wscid: null, Name: "Garma Ivesove" });
    }

    [Fact]
    public void ParseRound_ReadsPositionsCallbacksAndScores()
    {
        var round = ScoringParser.ParseRound(Fixture("scoring-prelim.html"))!;

        var winner = round.Entries.Single(e => e.Wscid == 21723);
        Assert.Equal(1, winner.Position);
        Assert.Equal("50", winner.Score);
        Assert.True(winner.Advanced);
        Assert.False(winner.IsAlternate);

        Assert.Equal(80, round.Entries.Count(e => e.Advanced));
        Assert.Equal(6, round.Entries.Count(e => e.IsAlternate));
        Assert.Contains(round.Entries, e => e.IsScratched);
    }

    [Fact]
    public void ParseRound_LabelsBothHalvesOfACoupleInAFinal()
    {
        var round = ScoringParser.ParseRound(Fixture("scoring-final.html"))!;

        // Finals list a leader and a follower per row, so roles are known without inference.
        var leader = round.Entries.Single(e => e.Wscid == 21682);
        Assert.Equal(Role.Leader, leader.Role);
        Assert.Equal(1, leader.Position);

        var partner = round.Entries.Single(e => e.Position == 1 && e.Role == Role.Follower);
        Assert.Equal("Lirena Vășcuță", partner.Name);
        Assert.Null(partner.Wscid);

        Assert.All(round.Entries, e => Assert.NotNull(e.Role));
    }

    [Theory]
    [InlineData("Novice Jack&Jill prelim", "NOV", RoundKind.Prelim)]
    [InlineData("All-Stars Jack&Jill final", "ALS", RoundKind.Final)]
    [InlineData("Intermediate Jack&Jill semi", "INT", RoundKind.Semi)]
    [InlineData("Champions Jack&Jill prelim", "CHMP", RoundKind.Prelim)]
    [InlineData("Masters Jack&Jill final", "MSTR", RoundKind.Final)]
    // Both are WSDC points divisions the registry lists; 437 mirrored rounds sat unclassified — and
    // so invisible to every per-division view — until the classifier knew them.
    [InlineData("Sophisticated Jack&Jill prelim", "SPH", RoundKind.Prelim)]
    [InlineData("Teacher Jack&Jill final", "TCH", RoundKind.Final)]
    // "Quarterfinal" contains "final" as a substring — Quarter must be checked first, or every
    // quarterfinal round would be counted as a final.
    [InlineData("Novice Jack&Jill quarterfinal", "NOV", RoundKind.Quarter)]
    [InlineData("Advanced Jack&Jill quarter final", "ADV", RoundKind.Quarter)]
    public void DivisionAndKind_AreReadFromTheRoundName(string roundName, string division, RoundKind kind)
    {
        Assert.Equal(division, ScoringParser.DivisionOf(roundName));
        Assert.Equal(kind, ScoringParser.KindOf(roundName));
    }

    [Fact]
    public void DivisionOf_ReturnsNullForRoundsOutsideTheWsdcDivisions()
    {
        Assert.Null(ScoringParser.DivisionOf("Rising Star final"));
        Assert.Null(ScoringParser.DivisionOf("All Brexit Jack&Jill prelim"));
    }

    [Theory]
    [InlineData("Novice Jack&Jill prelim", true)]
    [InlineData("Jack&Jill Newcomer ( WSDC ) final", true)]
    [InlineData("Masters Jack&Jill (≥50y) prelim", true)]
    [InlineData("Advanced/All-Stars Jack&Jill final", true)]
    [InlineData("WCS Intermediate Jack&Jill semi", true)]
    // Events that print only the division mean its Jack & Jill; nothing else is claimed.
    [InlineData("Newcomer prelim", true)]
    // A round that calls itself a Jack & Jill is one, however the event dresses up the rest.
    [InlineData("Masters Open Jack&Jill prelim", true)]
    [InlineData("Advanced /All Star Jack&Jill final", true)]
    [InlineData("Jack&Jill All-Stars/Champ semi", true)]
    [InlineData("Jack&Jill (Non-Professional) prelim", true)]
    [InlineData("Junior Jack&Jill official (<18y) final", true)]
    // Except where it also names another format, which a Pro-Am J&J does.
    [InlineData("Pro-Am Novice J&J (AmL) final", false)]
    [InlineData("Strictly Novice final", false)]
    [InlineData("Strictly Newcomer/Novice prelim", false)]
    [InlineData("Pro-Am Novice (Am L) final", false)]
    [InlineData("Advanced SILC Fixed Partner quarter", false)]
    [InlineData("Role Rotation Novice semi", false)]
    [InlineData("Intermediate Chill-Out final", false)]
    [InlineData("Country Two-Step Newcomer/Novice prelim", false)]
    [InlineData("Junior Shag final", false)]
    [InlineData("Icebreaker Jack & Jill prelim", false)]
    public void IsJackAndJill_SeparatesTheDivisionsContestFromEveryOtherFormat(string roundName, bool expected) =>
        Assert.Equal(expected, ScoringParser.IsJackAndJill(roundName));

    [Fact]
    public void ParseEventIds_FindsEventsWithPublishedResults()
    {
        var ids = ScoringParser.ParseEventIds(Fixture("scoring-event.html"));

        Assert.Contains(201, ids);
        Assert.Equal(ids.Distinct().Count(), ids.Count);
    }

    [Fact]
    public void ParseRound_SkipsACoupleWhosePlacementIsNull()
    {
        // TryGetInt32 throws on a non-number element, so a null placement used to abort the sync.
        var html = Fixture("scoring-final.html")
            .Replace("\"placement\":1,", "\"placement\":null,", StringComparison.Ordinal);

        var round = ScoringParser.ParseRound(html)!;

        Assert.DoesNotContain(round.Entries, e => e.Position == 1);
        Assert.NotEmpty(round.Entries);
    }

    [Fact]
    public void ParseRound_ReadsEveryJudgesCallbackMark()
    {
        var round = ScoringParser.ParseRound(Fixture("scoring-prelim.html"))!;
        var winner = round.Entries.Single(e => e.Wscid == 21723);

        // Five judges marked this dancer; the panel's chief left their cell blank.
        Assert.Equal(5, winner.Marks.Count);
        Assert.All(winner.Marks, m => Assert.Equal("Yes", m.Mark));
        Assert.All(winner.Marks, m => Assert.Equal(10, m.Points));
        Assert.Contains(winner.Marks, m => m.Judge == "Odger Falköny");

        // The chief judge's name carries a role in the tooltip that is not part of the name.
        Assert.DoesNotContain(round.Entries.SelectMany(e => e.Marks), m => m.Judge.Contains('('));
    }

    [Fact]
    public void ParseRound_ScoresAlternateMarksBelowAYes()
    {
        var round = ScoringParser.ParseRound(Fixture("scoring-prelim.html"))!;

        var marks = round.Entries.SelectMany(e => e.Marks).ToList();
        Assert.Equal(0, marks.First(m => m.Mark == "No").Points);
        Assert.Equal(4.5, marks.First(m => m.Mark == "Alt1").Points);
        Assert.Equal(4.2, marks.First(m => m.Mark == "Alt3").Points);
    }

    [Fact]
    public void ParseRound_ReadsEachJudgesPlacementInAFinal()
    {
        var round = ScoringParser.ParseRound(Fixture("scoring-final.html"))!;
        var winner = round.Entries.Single(e => e.Wscid == 21682);

        Assert.Equal(7, winner.Marks.Count);
        Assert.All(winner.Marks, m => Assert.NotNull(m.Placement));
        Assert.All(winner.Marks, m => Assert.Null(m.Points));

        // Six of seven judges placed the winning couple first; one had them third.
        Assert.Equal(1, winner.Marks.Single(m => m.Judge == "Halis Kadrane").Placement);
        Assert.Equal(8, winner.Marks.Single(m => m.Judge == "Hallin Raskstad").Placement);
    }

    /// <summary>Wraps a final's <c>result[]</c> array in the minimum page the parser needs.</summary>
    private static string FinalPage(string resultJson) =>
        "<html><body><script type=\"application/ld+json\">" +
        "{\"@context\":\"https://schema.org\",\"@type\":\"DanceEvent\"," +
        "\"name\":\"Test Open 2025\",\"round\":{\"id\":9001,\"name\":\"All-Stars Jack&Jill final\"}," +
        "\"startDate\":\"2025-05-01\",\"result\":" + resultJson + "}" +
        "</script></body></html>";

    [Fact]
    public void ParseRound_KeepsBothDancesWhenACompetitorPlacesTwiceInAnUnevenFinal()
    {
        // Uneven leader/follower counts: the same follower (id 500) danced a second time and was
        // ranked again on her own merits. Both placements are real (WSDC awards points for the
        // higher one), so both entries must survive.
        var round = ScoringParser.ParseRound(FinalPage(
            """
            [
              {"dancer":{"leader":{"bib":"11","fullname":"Lead One","wsdc":{"id":"100"}},
                         "follower":{"bib":"22","fullname":"Twice Danced","wsdc":{"id":"500"}}},
               "placement":1,"judges_placements":[{"name":"J A","placement":"1"},{"name":"J B","placement":"1"}]},
              {"dancer":{"leader":{"bib":"33","fullname":"Lead Two","wsdc":{"id":"200"}},
                         "follower":{"bib":"22","fullname":"Twice Danced","wsdc":{"id":"500"}}},
               "placement":2,"judges_placements":[{"name":"J A","placement":"4"},{"name":"J B","placement":"5"}]}
            ]
            """))!;

        var hers = round.Entries.Where(e => e.Wscid == 500).OrderBy(e => e.Position).ToList();
        Assert.Equal(2, hers.Count);
        Assert.All(hers, e => Assert.Equal(Role.Follower, e.Role));
        Assert.Equal([1, 2], hers.Select(e => e.Position));

        // Two separate dances, not one row copied: the judge placements differ.
        Assert.Equal(1, hers[0].Marks.Single(m => m.Judge == "J A").Placement);
        Assert.Equal(4, hers[1].Marks.Single(m => m.Judge == "J A").Placement);
    }

    [Fact]
    public void ParseRound_CollapsesACoupleThatNamesOnePersonAsBothHalves()
    {
        // Seen in some Pro-Am and Strictly finals: scoring.dance copied the amateur's name and id
        // into the partner slot too. One placement is real; the role is not recoverable.
        var round = ScoringParser.ParseRound(FinalPage(
            """
            [
              {"dancer":{"leader":{"bib":"7","fullname":"Pat Amateur","wsdc":{"id":"700"}},
                         "follower":{"bib":"7","fullname":"Pat Amateur","wsdc":{"id":"700"}}},
               "placement":1,"judges_placements":[{"name":"J A","placement":"1"}]},
              {"dancer":{"leader":{"bib":"8","fullname":"Real Lead","wsdc":{"id":"800"}},
                         "follower":{"bib":"9","fullname":"Real Follow","wsdc":{"id":"900"}}},
               "placement":2,"judges_placements":[{"name":"J A","placement":"2"}]}
            ]
            """))!;

        var pat = Assert.Single(round.Entries, e => e.Wscid == 700);
        Assert.Null(pat.Role);
        Assert.Equal(1, pat.Position);

        // The well-formed couple is untouched.
        Assert.Equal(Role.Leader, round.Entries.Single(e => e.Wscid == 800).Role);
        Assert.Equal(Role.Follower, round.Entries.Single(e => e.Wscid == 900).Role);
    }

    [Fact]
    public void ParseRound_ReadsIdsBibsAndNamesSentAsNumbers()
    {
        var round = ScoringParser.ParseRound(FinalPage(
            """
            [
              {"dancer":{"leader":{"bib":11,"fullname":"Lead One","wsdc":{"id":100}},
                         "follower":{"bib":"22","fullname":2024,"wsdc":{"id":"200"}}},
               "placement":1,"judges_placements":[{"name":7,"placement":"1"}]}
            ]
            """))!;

        var lead = round.Entries.Single(e => e.Role == Role.Leader);
        Assert.Equal(100, lead.Wscid);
        Assert.Equal("11", lead.Bib);
        Assert.Equal("7", lead.Marks.Single().Judge);
        Assert.Equal("2024", round.Entries.Single(e => e.Role == Role.Follower).Name);
    }

    [Fact]
    public void ParseRound_DoesNotGiveTheLeadersBibToTheFollower()
    {
        // A Strictly prints one bib per couple — the leader's. Copying it onto the follower makes
        // the pair look like one dancer, and bib linking then hands her his WSDC id.
        var round = ScoringParser.ParseRound(Fixture("scoring-final.html"))!;

        var leaders = round.Entries.Where(e => e.Role == Role.Leader).ToList();
        var followers = round.Entries.Where(e => e.Role == Role.Follower).ToList();

        Assert.NotEmpty(leaders);
        Assert.All(leaders, e => Assert.NotNull(e.Bib));

        // No bib may belong to a leader and a follower at once.
        var leaderBibs = leaders.Select(e => e.Bib).ToHashSet();
        Assert.DoesNotContain(followers.Where(f => f.Bib is not null), f => leaderBibs.Contains(f.Bib));
    }

    [Fact]
    public void ParseUpcomingEvents_ReadsTheListingTheHomePageCarriesInline()
    {
        var events = ScoringParser.ParseUpcomingEvents(Fixture("scoring-home.html"));

        // 25 in the array, one of which is the "add your event" placeholder.
        Assert.Equal(24, events.Count);

        var bavarian = events.Single(e => e.Id == 438);
        Assert.Equal("Bavarian Open 2026", bavarian.Name);
        Assert.Equal(new DateOnly(2026, 9, 10), bavarian.DateFrom);
        Assert.Equal(new DateOnly(2026, 9, 14), bavarian.DateTo);
        Assert.Equal("München", bavarian.City);
        Assert.Equal("DEU", bavarian.CountryCode);
        Assert.True(bavarian.IsWsdc);
        Assert.Equal("https://bavarianopen.com/registration", bavarian.TicketUrl);
    }

    [Fact]
    public void ParseUpcomingEvents_SkipsThePlaceholderRowInvitingOrganisersToAddOne()
    {
        var events = ScoringParser.ParseUpcomingEvents(Fixture("scoring-home.html"));

        Assert.DoesNotContain(events, e => e.Name.StartsWith("***", StringComparison.Ordinal));
    }

    [Fact]
    public void ParseUpcomingEvents_ReturnsNothingWhenThePageCarriesNoListing()
    {
        Assert.Empty(ScoringParser.ParseUpcomingEvents("<html><body>No events found</body></html>"));
    }
}
