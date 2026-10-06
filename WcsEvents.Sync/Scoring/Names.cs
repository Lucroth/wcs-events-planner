using System.Globalization;
using System.Text;

namespace WcsEvents.Sync.Scoring;

/// <summary>
/// Compares dancer names across the two sources. The registry stores many names stripped to ASCII
/// while scoring.dance keeps the real spelling, so "Ormila Brastonek" and "Ormiła Brastonek"
/// are one person and must compare equal.
/// </summary>
public static class Names
{
    /// <summary>Letters that carry no accent to strip — they are simply different letters.</summary>
    private static readonly Dictionary<char, string> Folded = new()
    {
        ['ł'] = "l",
        ['ø'] = "o",
        ['đ'] = "d",
        ['ð'] = "d",
        ['þ'] = "th",
        ['ß'] = "ss",
        ['æ'] = "ae",
        ['œ'] = "oe",
        ['ı'] = "i",
    };

    /// <summary>Lowercased, stripped of accents and punctuation, with runs of space collapsed.</summary>
    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(name.Length);

        foreach (var character in name.ToLowerInvariant().Normalize(NormalizationForm.FormD))
        {
            if (Folded.TryGetValue(character, out var replacement))
            {
                builder.Append(replacement);
            }
            else if (char.IsLetter(character) || char.IsDigit(character))
            {
                builder.Append(character);
            }
            else if (CharUnicodeInfo.GetUnicodeCategory(character) is not UnicodeCategory.NonSpacingMark)
            {
                // Punctuation and separators both become a single space.
                builder.Append(' ');
            }
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public static bool Same(string? one, string? other) =>
        Normalize(one) is { Length: > 0 } left && left == Normalize(other);

    /// <summary>
    /// Whether two names can be told apart at all. A name of nothing but very short words — "Ba Yo",
    /// or an empty registry entry — leaves nothing to compare, and reading that as a mismatch would
    /// reject a dancer against their own name. Names written in different scripts are also not
    /// comparable this way: the registry romanises everything, so "Тимур Расколов" and its
    /// registry form "Timur Raskolov" share no literal word and a word test would wrongly call
    /// them different people.
    /// </summary>
    public static bool Comparable(string? one, string? other) =>
        Words(one).Count > 0 && Words(other).Count > 0 && IsLatin(one) == IsLatin(other);

    /// <summary>Whether the name, once normalised, is written entirely in the Latin alphabet.</summary>
    private static bool IsLatin(string? name) =>
        Normalize(name).All(c => c is ' ' or (>= '0' and <= '9') or (>= 'a' and <= 'z'));

    /// <summary>
    /// Whether two names share a word of real length, which tolerates reordering, middle initials
    /// and the two sources spelling a name differently.
    /// </summary>
    public static bool SharesAWord(string? one, string? other)
    {
        var words = Words(one);
        return words.Count > 0 && words.Overlaps(Words(other));
    }

    public static HashSet<string> Words(string? name)
    {
        HashSet<string> words = new(StringComparer.Ordinal);

        foreach (var word in Normalize(name).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            // Initials and short particles say nothing about identity.
            if (word.Length >= 3)
            {
                words.Add(word);
            }
        }

        return words;
    }
}
