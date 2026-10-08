using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Microsoft.Extensions.Caching.Memory;
using WcsEvents.Sync.Scoring;

namespace WcsEvents.Sync.Calendar;

/// <summary>
/// One upcoming WSDC-sanctioned edition from worldsdc.com/events: its dates, the organiser's website
/// and where it is. <paramref name="Address"/> is the venue's street address with city and country,
/// as far as the WSDC has it, ready to geocode.
/// </summary>
public sealed record CalendarEvent(string Name, DateOnly From, DateOnly To, string? WebsiteUrl, string? City, string? Country, string? Address);

/// <summary>
/// Reads the WSDC calendar, the authority on which upcoming events are sanctioned: scoring.dance
/// lists an event only once its organisers have set it up there, often months after the WSDC has it.
/// The page carries every event as schema.org JSON-LD next to its visible table, with exact dates and
/// the venue's address; that is what is read. Fetched once an hour at most.
/// </summary>
public sealed partial class WsdcCalendar(HttpClient http, IMemoryCache cache, ILogger<WsdcCalendar> logger)
{
    public const string PageUrl = "https://worldsdc.com/events/";

    /// <summary>Fewer rows than this means the page changed shape, not that the WSDC has no events.</summary>
    private const int MinPlausibleRows = 20;

    /// <summary>The calendar's rows, or null when it could not be read: callers then leave what they published alone.</summary>
    public async Task<IReadOnlyList<CalendarEvent>?> LoadAsync(CancellationToken ct) =>
        await cache.GetOrCreateAsync("wsdc-calendar", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);
            try
            {
                var rows = Parse(await http.GetStringAsync("events/", ct));
                if (rows.Count >= MinPlausibleRows)
                {
                    return rows;
                }

                LogTooFew(logger, rows.Count);
            }
            catch (HttpRequestException ex)
            {
                LogUnreachable(logger, ex);
            }

            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            return null;
        });

    public static IReadOnlyList<CalendarEvent> Parse(string html)
    {
        HtmlDocument doc = new();
        doc.LoadHtml(html);
        List<CalendarEvent> events = [];

        foreach (var script in doc.DocumentNode.SelectNodes("//script[@type='application/ld+json']") ?? new HtmlNodeCollection(null))
        {
            try
            {
                using var json = JsonDocument.Parse(script.InnerText);
                IEnumerable<JsonElement> items = json.RootElement.ValueKind is JsonValueKind.Array ? json.RootElement.EnumerateArray() : [json.RootElement];
                events.AddRange(items.Select(Read).OfType<CalendarEvent>());
            }
            catch (JsonException)
            {
                // Another kind of block on the page; the events are in the large one.
            }
        }

        return events;
    }

    private static CalendarEvent? Read(JsonElement e)
    {
        if (e.ValueKind is not JsonValueKind.Object
            || Str(e, "@type") != "Event"
            || Str(e, "name") is not { Length: > 0 } rawName
            || Date(e, "startDate") is not { } from)
        {
            return null;
        }

        // The organisers' own marker for a year the event skips; it is on the calendar but not happening.
        var name = WebUtility.HtmlDecode(rawName).Trim();
        if (name.Contains("hiatus", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var address = e.TryGetProperty("location", out var place) && place.ValueKind is JsonValueKind.Object && place.TryGetProperty("address", out var a) ? a : default;
        var street = Str(address, "streetAddress");
        var city = Tidy(Str(address, "addressLocality"));
        var country = Countries.Canonical(Str(address, "addressCountry")) ?? CountryFromAddress(street);

        return new CalendarEvent(name, from, Date(e, "endDate") ?? from, CleanUrl(Str(e, "url")), city, country, FullAddress(street, city, country));
    }

    /// <summary>The street, city and country as one line to geocode; parts the street already names are not repeated.</summary>
    internal static string? FullAddress(string? street, string? city, string? country)
    {
        if (street is not { Length: > 0 })
        {
            return null;
        }

        List<string> parts = [street];
        foreach (var part in new[] { city, country })
        {
            if (part is { Length: > 0 } && !parts.Exists(p => p.Contains(part, StringComparison.OrdinalIgnoreCase)))
            {
                parts.Add(part);
            }
        }

        return string.Join(", ", parts);
    }

    /// <summary>A few entries leave the country empty; some street addresses end with it.</summary>
    private static string? CountryFromAddress(string? street) =>
        street?.Split(',')[^1].Trim() is { Length: > 2 } last && Countries.IsEuropean(last) ? Countries.Canonical(last) : null;

    /// <summary>"paris" and "BLAGNAC" are typed in one case; other spellings are left as the organiser wrote them.</summary>
    internal static string? Tidy(string? city) =>
        city is not null && (city == city.ToLowerInvariant() || city == city.ToUpperInvariant())
            ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(city.ToLowerInvariant())
            : city;

    /// <summary>The organiser's link as the WSDC typed it: a doubled scheme ("http://Https://x.com") or none at all.</summary>
    internal static string? CleanUrl(string? raw)
    {
        var url = WebUtility.HtmlDecode(raw)?.Trim();
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        url = DoubledScheme().Replace(url, "");
        if (!url.Contains("://", StringComparison.Ordinal))
        {
            url = "https://" + url;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.ToString() : null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind is JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String && v.GetString()?.Trim() is { Length: > 0 } s ? s : null;

    /// <summary>The date part of "2026-12-11T00:00:00.000000Z": the events are dated in their own place, not in UTC.</summary>
    private static DateOnly? Date(JsonElement e, string name) =>
        Str(e, name) is { Length: >= 10 } s && DateOnly.TryParseExact(s[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    [GeneratedRegex(@"^https?://(?=https?://)", RegexOptions.IgnoreCase)]
    private static partial Regex DoubledScheme();

    [LoggerMessage(Level = LogLevel.Warning, Message = "The WSDC calendar listed {Rows} events, too few to trust; the page may have changed")]
    private static partial void LogTooFew(ILogger logger, int rows);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The WSDC calendar could not be fetched")]
    private static partial void LogUnreachable(ILogger logger, Exception ex);
}
