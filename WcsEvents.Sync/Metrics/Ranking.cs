namespace WcsEvents.Sync.Metrics;

public static class Ranking
{
    public static double Median(IReadOnlyList<double> sorted) =>
        sorted.Count % 2 is 1
            ? sorted[sorted.Count / 2]
            : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2.0;
}
