using System.Text.RegularExpressions;

namespace WcsEvents.Sync.Metrics;

/// <summary>
/// Whether two rows describe the same real-world event, across the registry and scoring.dance.
///
/// The two sources name events differently — the registry prints the full title ("BudaFest Open WCS
/// Championships"), scoring.dance a short one with the year ("Budafest 2023") — and differ freely in
/// punctuation and case. Matching therefore compares letters and digits only, with a trailing
/// four-digit year stripped, and treats one name containing the other as a match.
///
/// Name alone is not enough: these events are annual, so "Boogie By the Bay 2025" and a 2008 "Boogie
/// By The Bay" normalize identically. Matches are also gated on the dates falling within a month of
/// each other — enough for the registry's first-of-the-month normalization and a New Year event, too
/// little to confuse two years of the same annual.
///
/// Used wherever a scoring.dance appearance is de-duped against a registry placement.
/// </summary>
public static partial class EventMatching
{
    /// <summary>How far apart two dates for the same event may sit. The registry normalizes many
    /// events to the first of the month, and an event over New Year straddles two of them.</summary>
    private const int MaxMonthsApart = 1;

    /// <summary>Shorter names must match exactly: "swing" is a substring of half the mirror. Measured
    /// against the shorter of the two, since that is the one being looked for inside the other.</summary>
    private const int MinContainmentLength = 6;

    public static bool SameEvent(string? a, DateOnly? dateA, string? b, DateOnly? dateB)
    {
        if (dateA is not { } left || dateB is not { } right || MonthsApart(left, right) > MaxMonthsApart)
        {
            return false;
        }

        return SameName(a, b);
    }

    /// <summary>The name half alone, for callers that have already established the two rows describe
    /// the same point in time.</summary>
    public static bool SameName(string? a, string? b)
    {
        var left = Normalize(a);
        var right = Normalize(b);

        if (left.Length is 0 || right.Length is 0)
        {
            return false;
        }

        return Math.Min(left.Length, right.Length) < MinContainmentLength
            ? left == right
            : left == right || left.Contains(right) || right.Contains(left);
    }

    /// <summary>
    /// Letters and digits only, lowercased, with a trailing four-digit year removed and "West Coast
    /// Swing" folded to "WCS". Exactly four digits, since a shorter or longer run is more likely part
    /// of the name (the "5280" in "5280 Swing Dance Championships"). The fold is what lets a registry
    /// "UK WCS Championships" reach a scoring.dance "UK West Coast Swing Championships 2025".
    /// </summary>
    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        // Edition markers go first, wherever they sit: "2026", the season form "2026/27" of New
        // Year events, and the "WSDC" / "Trial Event" tags some organisers append. Only 19xx/20xx
        // count as years, so the "5280" in "5280 Swing Dance Championships" stays part of the name.
        var stripped = EditionMarkers().Replace(name, " ");

        var letters = new string([.. stripped.Where(char.IsLetterOrDigit)]).ToLowerInvariant();

        var trimEnd = letters.Length;
        while (trimEnd > 0 && char.IsDigit(letters[trimEnd - 1]))
        {
            trimEnd--;
        }

        var trimmed = letters.Length - trimEnd == 4 ? letters[..trimEnd] : letters;

        return trimmed.Replace("westcoastswing", "wcs");
    }

    /// <summary>The name without its edition markers ("Budafest 2026" and "SwingVester 2026/27 WSDC" give "Budafest" and "SwingVester").</summary>
    public static string SeriesName(string name) =>
        Spaces().Replace(EditionMarkers().Replace(name, " "), " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"(?<!\d)(19|20)\d{2}(\s*[/-]\s*((19|20)\d{2}|\d{2}))?(?!\d)|\bWSDC\b|\bTrial\s+Event\b", RegexOptions.IgnoreCase)]
    private static partial Regex EditionMarkers();

    private static int MonthsApart(DateOnly a, DateOnly b) =>
        Math.Abs(((a.Year - b.Year) * 12) + a.Month - b.Month);
}
