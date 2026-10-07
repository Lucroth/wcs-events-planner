using WcsEvents.Sync.Data;
using WcsEvents.Sync.Metrics;
using WcsEvents.Sync.Publish;

namespace WcsEvents.Tests;

public sealed class EventLogicTests
{
    [Fact]
    public void Difficulty_SplitsEventsIntoThirds()
    {
        double[] sorted = [1, 2, 3, 4, 5, 6];

        Assert.Equal(Difficulty.Easy, Strength.Classify(1, sorted));
        Assert.Equal(Difficulty.Medium, Strength.Classify(3, sorted));
        Assert.Equal(Difficulty.Hard, Strength.Classify(6, sorted));
        Assert.Null(Strength.Classify(1, [1, 2]));
    }

    [Fact]
    public void TopQuartile_AveragesTheStrongestQuarterAndAtLeastOne()
    {
        Assert.Equal(9.5, Strength.TopQuartileAverage([0, 0, 0, 0, 0, 1, 9, 10]));
        Assert.Equal(7, Strength.TopQuartileAverage([0, 7]));
        Assert.Equal(4, Strength.TopQuartileAverage([4]));
    }

    [Fact]
    public void PointsHistory_CountsOnlyMonthsBeforeTheEvent()
    {
        var history = new Strength.PointsHistory(
        [
            (1, new DateOnly(2026, 1, 1), 3),
            (1, new DateOnly(2026, 3, 1), 5),
            (1, null, 2),
        ]);

        Assert.Equal(5, history.AsOf(1, new DateOnly(2026, 3, 14)));
        Assert.Equal(10, history.AsOf(1, new DateOnly(2026, 4, 1)));
        Assert.Equal(0, history.AsOf(2, new DateOnly(2026, 4, 1)));
    }


    [Fact]
    public void ExpectedEditions_ProjectsOnlyTheLatestEditionStillToRecur()
    {
        static EventFacts Facts(int id, string name, int year, int month) =>
            new(new ScoringEvent { Id = id, Name = name, DateFrom = new DateOnly(year, month, 10) }, true, [], null, []);

        List<EventFacts> all = [Facts(1, "Budafest 2025", 2025, 1), Facts(2, "Budafest 2026", 2026, 1), Facts(3, "Swiss Open 2026", 2026, 12), Facts(4, "Old Fest 2024", 2024, 5), Facts(5, "Cologne Calling 2027", 2027, 1)];

        var expected = EventPublisher.ExpectedEditions(all, new DateOnly(2026, 10, 6)).Select(f => f.Event.Id).ToList();

        Assert.Equal([2, 3, 5], expected);
    }

    [Fact]
    public void ExpectedRow_UsesAnnouncedDatesOnlyWhenTheyAreLater()
    {
        EventFacts last = new(new ScoringEvent { Id = 347, Name = "Berlin Swing Revolution 2025", DateFrom = new DateOnly(2025, 12, 11), DateTo = new DateOnly(2025, 12, 14), City = "Berlin" }, true, [], null, []);
        Announcement next = new(new DateOnly(2026, 12, 10), new DateOnly(2026, 12, 13), null, "Werk36", "https://berlinswingrevolution.com/", null);
        Announcement stale = next with { DateFrom = new DateOnly(2025, 12, 11) };

        var announced = EventPublisher.Row.Expected(last, next);
        var guessed = EventPublisher.Row.Expected(last, stale);

        Assert.Equal((new DateOnly(2026, 12, 10), new DateOnly(2026, 12, 13), "Berlin"), (announced.From, announced.To, announced.City));
        Assert.Equal(new DateOnly(2026, 12, 11), guessed.From);
        Assert.Null(guessed.Announcement);
    }
}
