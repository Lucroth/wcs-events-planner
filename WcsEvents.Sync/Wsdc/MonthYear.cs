using System.Globalization;

namespace WcsEvents.Sync.Wsdc;

/// <summary>Parses the registry's "August 2018" event dates to the first of that month.</summary>
public static class MonthYear
{
    private static readonly string[] Formats = ["MMMM yyyy", "MMM yyyy"];

    public static DateOnly? Parse(string? raw) =>
        !string.IsNullOrWhiteSpace(raw)
        && DateTime.TryParseExact(raw.Trim(), Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? DateOnly.FromDateTime(dt)
            : null;
}
