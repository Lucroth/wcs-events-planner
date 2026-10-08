using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using WcsEvents.Sync.Data;
using WcsEvents.Sync.Metrics;
using WcsEvents.Sync.Scoring;

namespace WcsEvents.Sync.Calendar;

/// <summary>
/// A calendar edition scoring.dance has no event for yet, published as an event of its own.
/// <see cref="Latest"/> is the most recent edition of the same series scoring.dance does know, whose
/// field strengths it borrows; null for a series new to the app.
/// </summary>
public sealed record PlannedEvent(string Id, CalendarEvent Entry, ScoringEvent? Latest)
{
    public string? Address => Entry.Address;

    /// <summary>The series name with this edition's year: "Budafest 2027", "Westie Gala 2026/27".</summary>
    public string Name => $"{EventMatching.SeriesName(Latest?.Name ?? Entry.Name)} {Edition}";

    private string Edition => Entry.To.Year != Entry.From.Year
        ? $"{Entry.From.Year}/{Entry.To.Year % 100:00}"
        : Entry.From.Year.ToString(CultureInfo.InvariantCulture);

    public string? Country => Entry.Country ?? Countries.Canonical(Latest?.Country);
}

/// <summary>
/// What the WSDC calendar adds to scoring.dance's events. Each upcoming European edition is either
/// the one scoring.dance lists (<see cref="Matched"/>: same series, dates within a month; the
/// calendar's dates win), or not listed there yet (<see cref="Added"/>).
/// </summary>
public sealed partial record CalendarPlan(IReadOnlyDictionary<int, CalendarEvent> Matched, IReadOnlyList<PlannedEvent> Added)
{
    public static CalendarPlan Build(IEnumerable<CalendarEvent> calendar, IReadOnlyList<ScoringEvent> events, DateOnly today)
    {
        Dictionary<int, CalendarEvent> matched = [];
        List<CalendarEvent> unlisted = [];

        foreach (var c in calendar.Where(c => c.To >= today).OrderBy(c => c.From))
        {
            var same = events
                .Where(e => e.DateFrom is not null && SameCountry(c, e) && MonthsApart(e.DateFrom.Value, c.From) <= 1 && SimilarName(e.Name, c.Name))
                .MinBy(e => Math.Abs(e.DateFrom!.Value.DayNumber - c.From.DayNumber));

            if (same is not null && matched.TryAdd(same.Id, c))
            {
                continue;
            }

            unlisted.Add(c);
        }

        // The first unlisted edition after a known one is "x" + its id; a later one adds its year.
        HashSet<string> taken = [];
        List<PlannedEvent> added = [];
        foreach (var c in unlisted)
        {
            var latest = events
                .Where(e => e.DateFrom < c.From && SameCountry(c, e) && SimilarName(e.Name, c.Name))
                .MaxBy(e => e.DateFrom);

            var id = latest is null
                ? $"w-{Slug(EventMatching.SeriesName(c.Name))}-{c.From.Year}"
                : taken.Contains($"x{latest.Id}") ? $"x{latest.Id}-{c.From.Year}" : $"x{latest.Id}";
            taken.Add(id);
            added.Add(new PlannedEvent(id, c, latest));
        }

        return new CalendarPlan(matched, added);
    }

    /// <summary>
    /// Sets an event's dates (and a missing website or city) to the calendar's, when the calendar lists
    /// that edition. The entities come from no-tracking queries, so nothing is written back.
    /// </summary>
    public bool Apply(ScoringEvent e)
    {
        if (!Matched.TryGetValue(e.Id, out var c))
        {
            return false;
        }

        e.DateFrom = c.From;
        e.DateTo = c.To;
        e.TicketUrl ??= c.WebsiteUrl;
        e.City ??= c.City;
        return true;
    }

    private static int MonthsApart(DateOnly a, DateOnly b) => Math.Abs(((a.Year - b.Year) * 12) + a.Month - b.Month);

    /// <summary>
    /// The same series by name. Beyond <see cref="EventMatching.SameName"/>, "WCS" is ignored, since
    /// organisers add or drop it between listings ("Scandinavian Open WCS [SNOW]" and "Scandinavian
    /// Open 2026 [SNOW]").
    /// </summary>
    internal static bool SimilarName(string a, string b)
    {
        if (EventMatching.SameName(a, b))
        {
            return true;
        }

        var (left, right) = (EventMatching.Normalize(a).Replace("wcs", ""), EventMatching.Normalize(b).Replace("wcs", ""));
        return Math.Min(left.Length, right.Length) >= 6 && (left == right || left.Contains(right, StringComparison.Ordinal) || right.Contains(left, StringComparison.Ordinal));
    }

    /// <summary>
    /// The calendar's European editions: those in a European country, and those that name no country
    /// (a few do) but belong to a series scoring.dance knows in Europe.
    /// </summary>
    public static IEnumerable<CalendarEvent> European(IEnumerable<CalendarEvent> calendar, IReadOnlyList<ScoringEvent> events) =>
        calendar.Where(c => Countries.IsEuropean(c.Country)
            || (c.Country is null && events.Any(e => Countries.IsEuropean(e.Country) && SimilarName(e.Name, c.Name))));

    /// <summary>Countries agree, or one side does not say.</summary>
    private static bool SameCountry(CalendarEvent c, ScoringEvent e) =>
        c.Country is null || e.Country is null || string.Equals(Countries.Canonical(e.Country), Countries.Canonical(c.Country), StringComparison.OrdinalIgnoreCase);

    /// <summary>"Düsseldorf" and "Duesseldorf", "Kraków" and "Krakow", "Warsaw" and "Warszawa" are one city.</summary>
    internal static bool SameCity(string a, string b) => Fold(a) == Fold(b);

    private static string Fold(string city)
    {
        var plain = new string([.. city.Normalize(NormalizationForm.FormD).Where(c => char.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && char.IsLetter(c))]).ToLowerInvariant();
        plain = plain.Replace("ue", "u").Replace("oe", "o").Replace("ae", "a");
        return plain switch { "warsaw" => "warszawa", "cracow" => "krakow", _ => plain };
    }

    internal static string Slug(string name) =>
        NonAlphanumeric().Replace(new string([.. name.Normalize(NormalizationForm.FormD).Where(c => char.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)]).ToLowerInvariant(), "-").Trim('-');

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlphanumeric();
}
