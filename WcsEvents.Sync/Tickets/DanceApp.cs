using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using WcsEvents.Sync.Calendar;

namespace WcsEvents.Sync.Tickets;

/// <summary>An event on DanceApp's list of upcoming events, with the title from its link ("Paris Swing Classic 2027 - WSDC").</summary>
public sealed record DanceAppEvent(int Id, string Title);

/// <summary>One price step of a pass: its deadline when DanceApp shows one, and whether it is on sale now or already over.</summary>
public sealed record TicketTier(string Name, decimal Price, DateOnly? Until, bool Current, bool Past);

/// <summary><paramref name="SoldOut"/>: DanceApp offers a waiting list instead of a purchase.</summary>
public sealed record TicketOffer(string Name, IReadOnlyList<TicketTier> Tiers, bool SoldOut = false);

/// <summary>A pass as the event page shows it; <paramref name="Kind"/> is "Full" or "Party".</summary>
public sealed record PassTier(string Kind, string Tier, decimal Price, string Currency, DateOnly? Until, bool Current, bool SoldOut = false);

/// <summary>
/// DanceApp (danceapp.net) sells the tickets of many European WCS events and shows each pass as a
/// card of price tiers with the deadline of each and the tier on sale now. Read daily, so passes
/// stay current without anyone retyping them.
/// </summary>
public sealed partial class DanceAppClient(HttpClient http)
{
    public async Task<IReadOnlyList<DanceAppEvent>> ListAsync(CancellationToken ct) =>
        ParseList(await http.GetStringAsync("en/events/", ct));

    /// <summary>Currency and passes of an event, or null when it has no ticket page.</summary>
    public async Task<(string Currency, IReadOnlyList<TicketOffer> Offers)?> TicketsAsync(int id, CancellationToken ct)
    {
        using var response = await http.GetAsync($"en/events/{id}/register", ct);
        return response.IsSuccessStatusCode ? ParseTickets(await response.Content.ReadAsStringAsync(ct)) : null;
    }

    public static IReadOnlyList<DanceAppEvent> ParseList(string html)
    {
        HtmlDocument doc = new();
        doc.LoadHtml(html);
        Dictionary<int, DanceAppEvent> events = [];

        foreach (var anchor in doc.DocumentNode.SelectNodes("//a[@href]") ?? new HtmlNodeCollection(null))
        {
            var match = EventLink().Match(anchor.GetAttributeValue("href", ""));
            if (match.Success && int.TryParse(match.Groups[1].Value, out var id))
            {
                events.TryAdd(id, new DanceAppEvent(id, WebUtility.UrlDecode(match.Groups[2].Value).Replace('_', ' ').Trim()));
            }
        }

        return [.. events.Values];
    }

    public static (string Currency, IReadOnlyList<TicketOffer> Offers)? ParseTickets(string html)
    {
        HtmlDocument doc = new();
        doc.LoadHtml(html);

        // Every ticket link carries the page's currency: "?currency=EUR&ticket=888".
        var currency = Currency().Match(html) is { Success: true } c ? c.Groups[1].Value : null;
        List<TicketOffer> offers = [];

        foreach (var card in doc.DocumentNode.SelectNodes("//div[contains(concat(' ', normalize-space(@class), ' '), ' ticket-card ')]") ?? new HtmlNodeCollection(null))
        {
            var name = Clean(card.SelectSingleNode(".//*[contains(@class,'ticket-name')]")?.InnerText);
            List<TicketTier> tiers = [];

            foreach (var row in card.SelectNodes(".//div[contains(@class,'tier-row')]") ?? new HtmlNodeCollection(null))
            {
                var classes = row.GetAttributeValue("class", "").Split(' ');
                if (ParsePrice(Clean(row.SelectSingleNode(".//*[contains(@class,'tier-price')]")?.InnerText)) is { } price)
                {
                    tiers.Add(new TicketTier(
                        Clean(row.SelectSingleNode(".//*[contains(@class,'tier-name')]")?.InnerText),
                        price,
                        ParseDeadline(Clean(row.SelectSingleNode(".//*[contains(@class,'tier-deadline')]")?.InnerText)),
                        classes.Contains("tier-current"),
                        classes.Contains("tier-old")));
                }
            }

            if (name.Length > 0 && tiers.Count > 0)
            {
                offers.Add(new TicketOffer(name, tiers, card.SelectSingleNode(".//*[contains(@class,'ticket-waitlist-info')]") is not null));
            }
        }

        return currency is null || offers.Count is 0 ? null : (currency, offers);
    }

    /// <summary>
    /// The Full and Party passes of an event's offers. Passes for All-Stars, judges, juniors, students,
    /// intensives, VIPs and the like are left out: they are not what a dancer plans a trip around. Past
    /// tiers are kept only when their deadline is known, so the page can show them as over.
    /// </summary>
    public static IReadOnlyList<PassTier> Passes(string currency, IEnumerable<TicketOffer> offers)
    {
        List<PassTier> passes = [];
        HashSet<string> seen = [];

        foreach (var offer in offers)
        {
            if (KindOf(offer.Name) is not { } kind || !seen.Add(kind))
            {
                continue;
            }

            passes.AddRange(offer.Tiers
                .Where(t => !t.Past || t.Until is not null)
                .Select(t => new PassTier(kind, t.Name, t.Price, currency, t.Until, t.Current, offer.SoldOut && t.Current)));
        }

        return passes;
    }

    internal static string? KindOf(string offerName)
    {
        var name = offerName.ToLowerInvariant();
        if (Excluded().IsMatch(name))
        {
            return null;
        }

        return name.Contains("party") || name.Contains("event pass") ? "Party" : name.Contains("full") ? "Full" : null;
    }

    /// <summary>"€190.00", "kr2,050.00", "£125.00": the amount, with a comma for thousands.</summary>
    internal static decimal? ParsePrice(string text)
    {
        var digits = Amount().Match(text).Value;
        if (digits.Length is 0)
        {
            return null;
        }

        digits = ThousandsCommas().IsMatch(digits) ? digits.Replace(",", "") : digits.Replace(',', '.');
        return decimal.TryParse(digits, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var price) && price > 0 ? price : null;
    }

    /// <summary>"until 2026-05-02 19:00 CEST": the day, in the event's own time.</summary>
    internal static DateOnly? ParseDeadline(string text) =>
        IsoDate().Match(text) is { Success: true } m && DateOnly.TryParseExact(m.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;

    /// <summary>
    /// Pairs events with their DanceApp page by name, and by year when the title has one. An event
    /// two pages could be is left alone rather than guessed.
    /// </summary>
    public static IReadOnlyDictionary<string, int> Match(IEnumerable<(string Id, string Name, DateOnly From, DateOnly To)> events, IReadOnlyList<DanceAppEvent> list)
    {
        Dictionary<string, int> matches = [];

        foreach (var (id, name, from, to) in events)
        {
            var candidates = list
                .Where(l => CalendarPlan.SimilarName(l.Title, name) && (TitleYear(l.Title) is not { } year || year == from.Year || year == to.Year))
                .ToList();

            if (candidates.Count is 1)
            {
                matches[id] = candidates[0].Id;
            }
        }

        return matches;
    }

    private static int? TitleYear(string title) =>
        TitleYearPattern().Match(title) is { Success: true } m ? int.Parse(m.Value, CultureInfo.InvariantCulture) : null;

    private static string Clean(string? text) =>
        text is null ? "" : Whitespace().Replace(WebUtility.HtmlDecode(text), " ").Trim();

    [GeneratedRegex(@"/events/(\d+)-([^/?#""]+)")]
    private static partial Regex EventLink();

    [GeneratedRegex(@"currency=([A-Z]{3})")]
    private static partial Regex Currency();

    [GeneratedRegex(@"[\d][\d.,]*")]
    private static partial Regex Amount();

    [GeneratedRegex(@"^\d{1,3}(,\d{3})+(\.\d+)?$")]
    private static partial Regex ThousandsCommas();

    [GeneratedRegex(@"\d{4}-\d\d-\d\d")]
    private static partial Regex IsoDate();

    [GeneratedRegex(@"(?<!\d)(19|20)\d{2}(?!\d)")]
    private static partial Regex TitleYearPattern();

    [GeneratedRegex(@"all.?star|champion|judge|junior|student|intensive|v\.?i\.?p|extreme|joker|family|teacher|volunteer|helper|solo|free")]
    private static partial Regex Excluded();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
