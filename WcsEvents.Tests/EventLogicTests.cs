using WcsEvents.Sync.Metrics;

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

}
