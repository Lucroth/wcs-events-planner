using WcsEvents.Sync.Publish;
using WcsEvents.Sync.Scoring;

namespace WcsEvents.Tests;

public sealed class LiveTests
{
    [Fact]
    public void ParseSchedule_CarriesTheDayDownAndHidesHiddenTimes()
    {
        var items = LivePublisher.ParseSchedule("""
            [{"dayname":"Friday, 10\/02\/2026","name":"Strictly Novice final","status":"99","time":"20:00"},
             {"dayname":"","name":"Jack&amp;Jill Novice ( WSDC ) prelim","status":"7","time":"21:00","time_start_hidden":"1"},
             {"dayname":"","name":"","status":"0","time":""}]
            """);

        Assert.Equal(2, items.Count);
        Assert.Equal(new LivePublisher.ScheduleItem("Strictly Novice final", "Friday, 10/02/2026", "20:00", 99), items[0]);
        Assert.Equal(new LivePublisher.ScheduleItem("Jack&Jill Novice ( WSDC ) prelim", "Friday, 10/02/2026", null, 7), items[1]);
        Assert.Equal("On the floor", LivePublisher.Label(7));
        Assert.Equal("Scoring", LivePublisher.Label(10));
    }

    [Fact]
    public void IsLive_CoversTheEventDatesWithADayEitherSide()
    {
        DateOnly from = new(2026, 10, 15), to = new(2026, 10, 18);

        Assert.True(LivePublisher.IsLive(from, to, new DateOnly(2026, 10, 14)));
        Assert.True(LivePublisher.IsLive(from, to, new DateOnly(2026, 10, 19)));
        Assert.False(LivePublisher.IsLive(from, to, new DateOnly(2026, 10, 20)));
    }

    private static string Fixture(string name) => File.ReadAllText(Path.Combine("Fixtures", name));

    [Fact]
    public void Result_ReadsCallbacksFromAPrelimAndPlacingsFromAFinal()
    {
        var prelim = LivePublisher.Result(ScoringParser.ParseRound(Fixture("scoring-live-prelim.html")));
        var final = LivePublisher.Result(ScoringParser.ParseRound(Fixture("scoring-live-final.html")));

        Assert.NotNull(prelim?.Advanced);
        Assert.Contains("Gerald Reschner", prelim.Advanced.SelectMany(t => t.Names));
        Assert.NotNull(final?.Placements);
        Assert.NotEmpty(final.Placements);
    }

    [Fact]
    public void Wall_LinksEveryScheduledRound()
    {
        var rounds = ScoringParser.ParseWallRounds(Fixture("scoring-wall-links.html")).Select(r => r.Name).ToHashSet();
        var schedule = LivePublisher.ParseSchedule(Fixture("scoring-schedule.json"));

        Assert.All(schedule, item => Assert.Contains(item.Name, rounds));
    }
}
