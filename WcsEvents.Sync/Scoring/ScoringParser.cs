using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using WcsEvents.Sync.Data;

namespace WcsEvents.Sync.Scoring;

public sealed record RoundLink(int RoundId, string Name);

/// <summary>One judge's mark for one dancer: callback points, or a placement in a final.</summary>
public sealed record ParsedMark(string Judge, string Mark, double? Points, int? Placement);

public sealed record ParsedEntry(
    int? Wscid,
    string Name,
    string? Bib,
    Role? Role,
    int TableIndex,
    int Position,
    string? Score,
    bool Advanced,
    bool IsAlternate,
    bool IsScratched)
{
    public IReadOnlyList<ParsedMark> Marks { get; init; } = [];
}

/// <summary>An event from the upcoming listing, before country codes are spelt out.</summary>
public sealed record ParsedEvent(
    int Id,
    string Name,
    DateOnly? DateFrom,
    DateOnly? DateTo,
    string? City,
    string? CountryCode,
    bool IsWsdc,
    string? TicketUrl);

public sealed record ParsedRound(
    int RoundId,
    string RoundName,
    string EventName,
    DateOnly? EventDate,
    string? Location,
    string? Country,
    IReadOnlyList<ParsedEntry> Entries);

/// <summary>
/// One dancer in one heat of a live event's marshalling wall — before any result exists, so there is
/// no score, placement, judge mark, or advancement flag to carry, only who is dancing, with whom, and
/// where in the running order.
/// </summary>
public sealed record WallEntry(int? Wscid, string Name, string? Bib, Role Role, int HeatNumber, int Position);

/// <summary>Reads scoring.dance result pages. Pure string in, records out.</summary>
public static partial class ScoringParser
{
    /// <summary>Round links on an event's results page, in publication order.</summary>
    public static IReadOnlyList<RoundLink> ParseRoundLinks(string html, int scoringEventId)
    {
        var doc = Load(html);
        var pattern = new Regex($@"/events/{scoringEventId}/results/(\d+)\.html$");

        List<RoundLink> links = [];
        HashSet<int> seen = [];

        foreach (var anchor in doc.DocumentNode.SelectNodes("//a[@href]") ?? new HtmlNodeCollection(null))
        {
            var match = pattern.Match(anchor.GetAttributeValue("href", string.Empty));
            if (match.Success && int.TryParse(match.Groups[1].Value, out var id) && seen.Add(id))
            {
                links.Add(new RoundLink(id, Clean(anchor.InnerText)));
            }
        }

        return links;
    }

    /// <summary>
    /// Round buttons on a live event's marshalling wall, in the order the event lists them — each
    /// button's own id is what GetWallRoundAsync needs to fetch that round's heats. Unlike
    /// ParseRoundLinks, these rounds may not have a single result published yet: the wall exists
    /// specifically so a round can be listed here before it's danced.
    /// </summary>
    public static IReadOnlyList<RoundLink> ParseWallRounds(string html)
    {
        var doc = Load(html);
        List<RoundLink> links = [];

        foreach (var button in doc.DocumentNode.SelectNodes("//button[@data-action='loadround']") ?? new HtmlNodeCollection(null))
        {
            if (int.TryParse(button.GetAttributeValue("data-id", string.Empty), out var id))
            {
                links.Add(new RoundLink(id, Clean(button.InnerText)));
            }
        }

        return links;
    }

    /// <summary>
    /// Every dancer entered in one round's heats, straight off the wall. A heat can be uneven (one
    /// side short a partner for that rotation), leaving that side's bib/name cells blank — those are
    /// simply skipped rather than yielded as an empty entry. Role comes straight from the table's own
    /// Leader/Follower columns, unlike a results page's prelim table, which needs role inferred later.
    /// A final has no "Heat N of M" header at all — there's only ever one group dancing — so a card
    /// with no header is heat 1, not skipped; the header is only there to distinguish one heat from
    /// another when a round actually splits into more than one.
    /// </summary>
    public static IReadOnlyList<WallEntry> ParseWallHeats(string html)
    {
        var doc = Load(html);
        List<WallEntry> entries = [];

        var cards = doc.DocumentNode.SelectNodes("//div[contains(concat(' ', normalize-space(@class), ' '), ' card ')]")
            ?? new HtmlNodeCollection(null);

        foreach (var card in cards)
        {
            var table = card.SelectSingleNode(".//table");
            var rows = table?.SelectNodes(".//tbody/tr");

            if (rows is null)
            {
                continue;
            }

            var header = card.SelectSingleNode(".//h3");
            var heatMatch = header is not null ? Regex.Match(header.InnerText, @"\d+") : null;
            var heatNumber = heatMatch is { Success: true } && int.TryParse(heatMatch.Value, out var parsed) ? parsed : 1;

            foreach (var row in rows)
            {
                var cells = row.SelectNodes("./td");
                if (cells is not { Count: >= 5 })
                {
                    continue;
                }

                var positionText = Clean(cells[2].InnerText);

                // A final's own alternates are listed below the field with an "ALT1", "ALT2", ... tag
                // in place of a running-order number — someone on it isn't dancing unless a scratch
                // calls them up, so they don't belong in a list of who's dancing right now.
                if (positionText.StartsWith("ALT", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var position = int.TryParse(positionText, out var pos) ? pos : 0;

                if (ReadWallDancer(cells[1]) is { } leader)
                {
                    entries.Add(new WallEntry(leader.Wscid, leader.Name, CleanBib(cells[0]), Role.Leader, heatNumber, position));
                }

                if (ReadWallDancer(cells[4]) is { } follower)
                {
                    entries.Add(new WallEntry(follower.Wscid, follower.Name, CleanBib(cells[3]), Role.Follower, heatNumber, position));
                }
            }
        }

        return entries;
    }

    /// <summary>Ids of events that have published results, from the "recent" listing.</summary>
    public static IReadOnlyList<int> ParseEventIds(string html)
    {
        var pattern = EventHrefRegex();
        HashSet<int> ids = [];

        foreach (Match match in pattern.Matches(html))
        {
            if (int.TryParse(match.Groups[1].Value, out var id))
            {
                ids.Add(id);
            }
        }

        return [.. ids.Order()];
    }

    /// <summary>
    /// Events the home page has not run yet. The listing itself is drawn by script from a JSON array
    /// the page carries inline, so the array is read rather than the markup: nothing is rendered
    /// server-side to scrape.
    /// </summary>
    public static IReadOnlyList<ParsedEvent> ParseUpcomingEvents(string html)
    {
        // The array literal, not the function of the same name declared earlier in the script.
        const string Marker = "processEvents([";

        var start = html.IndexOf(Marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return [];
        }

        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(html[(start + Marker.Length - 1)..]));
        if (!JsonDocument.TryParseValue(ref reader, out var document))
        {
            return [];
        }

        using (document)
        {
            if (document.RootElement.ValueKind is not JsonValueKind.Array)
            {
                return [];
            }

            List<ParsedEvent> events = [];

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (ReadEvent(element) is { } parsed)
                {
                    events.Add(parsed);
                }
            }

            return events;
        }
    }

    /// <summary>
    /// One listing entry. Every value arrives as a string, and each field is also present under its
    /// column number, which is why fields are read by name rather than by position.
    /// </summary>
    private static ParsedEvent? ReadEvent(JsonElement element)
    {
        if (element.ValueKind is not JsonValueKind.Object
            || Text(element, "id") is not { } rawId
            || !int.TryParse(rawId, out var id)
            || Text(element, "name") is not { } name)
        {
            return null;
        }

        // The listing ends with a placeholder row inviting organisers to add their own event.
        if (name.StartsWith("***", StringComparison.Ordinal))
        {
            return null;
        }

        return new ParsedEvent(
            id,
            Clean(name),
            ReadListingDate(element, "dt_event_from"),
            ReadListingDate(element, "dt_event_to"),
            Text(element, "city") is { } city ? Clean(city) : null,
            Text(element, "country"),
            Text(element, "is_wsdc") is "1",
            Text(element, "url_ticket") is { Length: > 0 } ticket ? ticket : null);

        static string? Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String
                ? value.GetString()
                : null;

        static DateOnly? ReadListingDate(JsonElement element, string name) =>
            Text(element, name) is { } raw
            && DateTime.TryParse(raw, CultureInfo.InvariantCulture, out var parsed)
                ? DateOnly.FromDateTime(parsed)
                : null;
    }

    public static ParsedRound? ParseRound(string html)
    {
        var doc = Load(html);
        var meta = ReadMetadata(doc);
        if (meta is null)
        {
            return null;
        }

        List<ParsedEntry> entries = [];
        var tables = doc.DocumentNode.SelectNodes("//table");

        for (var tableIndex = 0; tableIndex < (tables?.Count ?? 0); tableIndex++)
        {
            var rows = tables![tableIndex].SelectNodes(".//tr[@data-rownum]");
            if (rows is null)
            {
                continue;
            }

            foreach (var row in rows)
            {
                entries.AddRange(ParseRow(row, tableIndex));
            }
        }

        // A final's structured result list names each half of every couple with its own bib, which
        // the HTML table omits — but only the table carries the scratched and alternate flags, so
        // the two are merged rather than one replacing the other.
        if (KindOf(meta.RoundName) is RoundKind.Final && ReadFinalResults(doc) is { Count: > 0 } placed)
        {
            entries = [.. Merge(placed, entries)];
        }

        return meta with { Entries = entries };
    }

    /// <summary>Structured final entries, taking per-row state from the matching table row.</summary>
    private static IEnumerable<ParsedEntry> Merge(List<ParsedEntry> placed, List<ParsedEntry> fromTable)
    {
        // Table rows are numbered by placement, the same ordering the result list uses.
        var stateByPosition = fromTable
            .GroupBy(e => e.Position)
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var entry in placed)
        {
            yield return stateByPosition.TryGetValue(entry.Position, out var row)
                ? entry with
                {
                    Advanced = row.Advanced,
                    IsAlternate = row.IsAlternate,
                    IsScratched = row.IsScratched,
                    Score = row.Score,
                }
                : entry;
        }
    }

    /// <summary>
    /// Couples from a final's JSON-LD, one entry per dancer, or empty when absent.
    /// <para>
    /// A dancer can legitimately show up twice here at different placements. When a final's leader
    /// and follower counts are uneven, a competitor dances a second time and is ranked again on
    /// their own merits; scoring.dance's couple-shaped <c>result[]</c> then carries them in two
    /// placement slots. The WSDC rules expect this ("If a competitor places twice in a division,
    /// the competitor is only awarded points for the higher placement") — both dances are real
    /// results, so both entries are kept and the "higher placement wins" rule is applied wherever
    /// points are attributed, not here.
    /// </para>
    /// <para>
    /// What is <em>not</em> real is the same person named as both halves of one couple, which
    /// scoring.dance has been seen publishing in some Pro-Am and Strictly finals (the amateur's
    /// name copied into the partner slot too). That collapses to a single, role-less entry below.
    /// </para>
    /// </summary>
    private static List<ParsedEntry> ReadFinalResults(HtmlDocument doc)
    {
        List<ParsedEntry> entries = [];

        if (FindDanceEvent(doc) is not { } root
            || !root.TryGetProperty("result", out var results)
            || results.ValueKind is not JsonValueKind.Array)
        {
            return entries;
        }

        foreach (var couple in results.EnumerateArray())
        {
            // TryGetInt32 throws rather than returning false when the value is not a number, and a
            // published final can carry a null placement.
            if (!couple.TryGetProperty("dancer", out var dancer)
                || !couple.TryGetProperty("placement", out var placementElement)
                || placementElement.ValueKind is not JsonValueKind.Number
                || !placementElement.TryGetInt32(out var placement))
            {
                continue;
            }

            var marks = ReadJudgePlacements(couple);

            var lead = Build(dancer, "leader", Role.Leader, placement, marks);
            var follow = Build(dancer, "follower", Role.Follower, placement, marks);

            if (lead is not null && follow is not null
                && lead.Wscid is { } sharedId && follow.Wscid == sharedId)
            {
                // One WSDC id on both sides of a single couple: a published-data slip, not a
                // person partnering themselves. The placement is real but the role is not
                // recoverable, so it is recorded once, unroled, rather than inventing a second
                // result for them in a role they may never have danced.
                entries.Add(lead with { Role = null });
            }
            else
            {
                if (lead is not null)
                {
                    entries.Add(lead);
                }

                if (follow is not null)
                {
                    entries.Add(follow);
                }
            }
        }

        return entries;

        static IReadOnlyList<ParsedMark> ReadJudgePlacements(JsonElement couple)
        {
            if (!couple.TryGetProperty("judges_placements", out var placements)
                || placements.ValueKind is not JsonValueKind.Array)
            {
                return [];
            }

            List<ParsedMark> marks = [];

            foreach (var judge in placements.EnumerateArray())
            {
                if (!judge.TryGetProperty("name", out var name)
                    || !judge.TryGetProperty("placement", out var given))
                {
                    continue;
                }

                // Placements arrive as strings here, unlike the couple's own placement.
                var raw = given.ValueKind is JsonValueKind.String ? given.GetString() : given.ToString();
                if (string.IsNullOrWhiteSpace(raw) || !int.TryParse(raw, out var value))
                {
                    continue;
                }

                marks.Add(new ParsedMark(Clean(Scalar(name)), raw, Points: null, value));
            }

            return marks;
        }

        static ParsedEntry? Build(JsonElement dancer, string key, Role role, int placement, IReadOnlyList<ParsedMark> marks)
        {
            if (!dancer.TryGetProperty(key, out var side))
            {
                return null;
            }

            var name = side.TryGetProperty("fullname", out var full) ? Clean(Scalar(full)) : string.Empty;
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            int? wscid = side.TryGetProperty("wsdc", out var wsdc)
                && wsdc.ValueKind is JsonValueKind.Object
                && wsdc.TryGetProperty("id", out var id)
                && int.TryParse(Scalar(id), out var parsed)
                    ? parsed
                    : null;

            return new ParsedEntry(
                wscid,
                name,
                side.TryGetProperty("bib", out var bib) ? Scalar(bib) : null,
                role,
                TableIndex: 0,
                placement,
                Score: null,
                Advanced: false,
                IsAlternate: false,
                IsScratched: false)
            {
                Marks = marks,
            };
        }
    }

    /// <summary>The page's DanceEvent JSON-LD block, cloned so it outlives the document.</summary>
    private static JsonElement? FindDanceEvent(HtmlDocument doc)
    {
        foreach (var script in doc.DocumentNode.SelectNodes("//script") ?? new HtmlNodeCollection(null))
        {
            var text = script.InnerText;
            if (!text.Contains("\"DanceEvent\"", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                using var json = JsonDocument.Parse(text.Trim());
                if (json.RootElement.TryGetProperty("round", out _))
                {
                    return json.RootElement.Clone();
                }
            }
            catch (JsonException)
            {
                // Not the block we want; keep looking.
            }
        }

        return null;
    }

    private static IEnumerable<ParsedEntry> ParseRow(HtmlNode row, int tableIndex)
    {
        var cells = row.SelectNodes("./td");
        if (cells is null || cells.Count < 2)
        {
            yield break;
        }

        var position = row.GetAttributeValue("data-rownum", 0);
        var state = row.GetAttributeValue("data-state", string.Empty);
        var scratched = row.GetAttributeValue("class", string.Empty).Contains("is_scratched", StringComparison.Ordinal);
        var bib = Clean(cells[0].InnerText);

        var score = row.SelectSingleNode(".//td[contains(@class,'text-right')]") is { } scoreCell
            ? Clean(scoreCell.InnerText)
            : null;

        // Cell 1 is the leader and cell 2 the follower when both are filled, matching how finals are
        // published. A Jack & Jill prelim fills only one, and its role is inferred later.
        var leader = ReadDancer(cells[1]);
        var follower = cells.Count > 2 ? ReadDancer(cells[2]) : null;
        var paired = leader is not null && follower is not null;

        if (leader is { } lead)
        {
            yield return Build(lead, paired ? Role.Leader : null, bib);
        }

        if (follower is { } follow)
        {
            // A paired row prints one bib, the leader's. Giving it to the follower too would make
            // the couple look like one dancer and hand her partner's identity to her.
            yield return Build(follow, Role.Follower, paired ? null : bib);
        }

        ParsedEntry Build((int? Wscid, string Name) dancer, Role? role, string? ownBib) => new(
            dancer.Wscid,
            dancer.Name,
            string.IsNullOrEmpty(ownBib) ? null : ownBib,
            role,
            tableIndex,
            position,
            string.IsNullOrEmpty(score) ? null : score,
            Advanced: state is "CB",
            IsAlternate: state.StartsWith("Alt", StringComparison.OrdinalIgnoreCase),
            IsScratched: scratched)
        {
            Marks = ReadMarks(row),
        };
    }

    /// <summary>
    /// Each judge's own cell, named by the tooltip the site puts on it. A callback round prints
    /// Yes/No/Alt; a final prints that judge's placement.
    /// </summary>
    private static IReadOnlyList<ParsedMark> ReadMarks(HtmlNode row)
    {
        var cells = row.SelectNodes("./td[@title]");
        if (cells is null)
        {
            return [];
        }

        List<ParsedMark> marks = [];

        foreach (var cell in cells)
        {
            var mark = Clean(cell.InnerText);
            if (string.IsNullOrEmpty(mark))
            {
                // The chief judge's cell is present but blank unless they broke a tie.
                continue;
            }

            var judge = JudgeName(cell.GetAttributeValue("title", string.Empty));
            if (string.IsNullOrEmpty(judge))
            {
                continue;
            }

            marks.Add(int.TryParse(mark, NumberStyles.Integer, CultureInfo.InvariantCulture, out var placement)
                ? new ParsedMark(judge, mark, Points: null, placement)
                : new ParsedMark(judge, mark, CallbackPoints(mark), Placement: null));
        }

        return marks;
    }

    /// <summary>
    /// What a callback mark is worth. A yes carries ten; the alternates carry a shade less each, so
    /// that a panel's totals break ties between dancers with the same number of yeses.
    /// </summary>
    private static double? CallbackPoints(string mark) => mark.ToLowerInvariant() switch
    {
        "yes" => 10,
        "no" => 0,
        "alt1" => 4.5,
        "alt2" => 4.3,
        "alt3" => 4.2,
        _ => null,
    };

    private static string JudgeName(string title)
    {
        var name = HtmlEntity.DeEntitize(title ?? string.Empty).Trim();

        // Titles read "Halden Marsh (Chiefjudge)"; the role is not part of the name.
        var bracket = name.IndexOf('(');
        return (bracket >= 0 ? name[..bracket] : name).Trim();
    }

    private static (int? Wscid, string Name)? ReadDancer(HtmlNode cell)
    {
        if (cell.SelectSingleNode(".//a[@data-wsdc]") is { } link)
        {
            var raw = link.GetAttributeValue("data-wsdc", string.Empty);

            // The site emits placeholders such as -1 and -6 for dancers it could not identify.
            return (int.TryParse(raw, out var wscid) && wscid > 0 ? wscid : null, Clean(link.InnerText));
        }

        var text = Clean(cell.InnerText);
        return string.IsNullOrEmpty(text) ? null : (null, text);
    }

    /// <summary>
    /// The wall page identifies a dancer with a plain link to their registry profile rather than the
    /// data-wsdc attribute results pages use — the only page found so far that does it this way.
    /// </summary>
    private static (int? Wscid, string Name)? ReadWallDancer(HtmlNode cell)
    {
        if (cell.SelectSingleNode(".//a[@href]") is { } link)
        {
            var match = WallRegistryLinkRegex().Match(link.GetAttributeValue("href", string.Empty));
            return (match.Success && int.TryParse(match.Groups[1].Value, out var wscid) ? wscid : null, Clean(link.InnerText));
        }

        var text = Clean(cell.InnerText);
        return string.IsNullOrEmpty(text) ? null : (null, text);
    }

    /// <summary>Null for an uneven heat's short side rather than an empty string, so a caller can tell
    /// "no bib printed" apart from a genuinely blank one.</summary>
    private static string? CleanBib(HtmlNode cell)
    {
        var text = Clean(cell.InnerText);
        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>
    /// Round and event details come from the JSON-LD block, which every round page carries — unlike
    /// the full results array, which only finals publish.
    /// </summary>
    private static ParsedRound? ReadMetadata(HtmlDocument doc)
    {
        foreach (var script in doc.DocumentNode.SelectNodes("//script") ?? new HtmlNodeCollection(null))
        {
            var text = script.InnerText;
            if (!text.Contains("\"DanceEvent\"", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                using var json = JsonDocument.Parse(text.Trim());
                var root = json.RootElement;

                if (!root.TryGetProperty("round", out var round)
                    || !round.TryGetProperty("id", out var idElement)
                    || idElement.ValueKind is not JsonValueKind.Number
                    || !idElement.TryGetInt32(out var roundId))
                {
                    continue;
                }

                var place = root.TryGetProperty("location", out var location) ? location : default;

                return new ParsedRound(
                    roundId,
                    round.TryGetProperty("name", out var name) ? Clean(name.GetString()) : string.Empty,
                    root.TryGetProperty("name", out var eventName) ? Clean(eventName.GetString()) : string.Empty,
                    ReadDate(root),
                    place.ValueKind is JsonValueKind.Object && place.TryGetProperty("name", out var city) ? city.GetString() : null,
                    place.ValueKind is JsonValueKind.Object && place.TryGetProperty("country", out var country) ? country.GetString() : null,
                    []);
            }
            catch (JsonException)
            {
                // Not the block we want; keep looking.
            }
        }

        return null;
    }

    /// <summary>
    /// scoring.dance has been seen shipping a round with real entrants and marks under an event dated
    /// a full year in the future ("City Of Angels 2026" itself listed as starting 2027-04-29) —
    /// evidently a mistyped year on their end, not a scheduling quirk, since a round with actual
    /// results can't genuinely be dated more than a couple of months ahead. Rejected here rather than
    /// stored and surfaced as someone's "last competed" date.
    /// </summary>
    private static DateOnly? ReadDate(JsonElement root)
    {
        if (!root.TryGetProperty("startDate", out var start)
            || !DateOnly.TryParse(start.GetString(), CultureInfo.InvariantCulture, out var date))
        {
            return null;
        }

        return date > DateOnly.FromDateTime(DateTime.Today).AddMonths(2) ? null : date;
    }

    /// <summary>Ordered longest-first so "All-Stars" is not matched by a shorter name.</summary>
    private static readonly (string Needle, string Abbreviation)[] Divisions =
    [
        ("newcomer", "NEW"),
        ("novice", "NOV"),
        ("intermediate", "INT"),
        ("advanced", "ADV"),
        ("all-star", "ALS"),
        ("allstar", "ALS"),
        ("all star", "ALS"),
        ("champion", "CHMP"),
        ("master", "MSTR"),
        ("junior", "JRS"),
        ("invitational", "INV"),
        ("professional", "PRO"),
        ("sophisticated", "SPH"),
        ("teacher", "TCH"),
    ];

    /// <summary>WSDC division for a round label like "Novice Jack&amp;Jill prelim"; null when it maps to none.</summary>
    public static string? DivisionOf(string roundName)
    {
        foreach (var (needle, abbreviation) in Divisions)
        {
            if (roundName.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return abbreviation;
            }
        }

        return null;
    }

    /// <summary>
    /// Contest formats that are not the division's Jack &amp; Jill. Naming one settles the question
    /// even where the round also says "Jack&amp;Jill", which a Pro-Am J&amp;J does.
    /// </summary>
    private static readonly string[] OtherFormats =
    [
        "strictly", "pro-am", "pro am", "proam", "routine", "silc", "chill", "two-step", "two step",
        "c2st", "shag", "fox", "rotation", "battle", "challenge", "choice", "showcase", "classic",
        "team", "icebreaker", "rising star",
    ];

    /// <summary>Ways a round says outright that it is a Jack &amp; Jill.</summary>
    private static readonly string[] JackAndJillNames =
        ["jack&jill", "jack & jill", "jack and jill", "j&j", "jnj"];

    /// <summary>
    /// Words that say nothing about the format: the round, and the badges events hang on a name.
    /// </summary>
    private static readonly string[] Noise =
        ["jack", "jill", "prelim", "semi", "quarter", "final", "wsdc", "wcs"];

    /// <summary>Names a contest that is not the division's Jack &amp; Jill: a Strictly, a Pro-Am, a routine.</summary>
    public static bool MentionsAnotherFormat(string text) =>
        OtherFormats.Any(format => text.Contains(format, StringComparison.OrdinalIgnoreCase));

    /// <summary>Says outright that it is a Jack &amp; Jill, under any of the names people give one.</summary>
    public static bool MentionsJackAndJill(string text) =>
        JackAndJillNames.Any(said => text.Contains(said, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether a round is the division's Jack &amp; Jill rather than a Strictly, a routine, a Pro-Am
    /// or one of the many one-off formats events invent. A named format wins, then a round that
    /// calls itself a Jack &amp; Jill — "Masters Open Jack&amp;Jill" is one however the event dresses
    /// up the name. Otherwise the test is what remains once the division, the round and the words for
    /// a Jack &amp; Jill are taken out, since plenty of events print only "Novice prelim" and mean a
    /// Jack &amp; Jill: nothing left means nothing else was claimed.
    /// </summary>
    public static bool IsJackAndJill(string roundName)
    {
        var name = roundName.ToLowerInvariant();

        if (MentionsAnotherFormat(name))
        {
            return false;
        }

        if (MentionsJackAndJill(name))
        {
            return true;
        }

        foreach (var needle in Divisions.Select(d => d.Needle).Concat(Noise))
        {
            name = name.Replace(needle, " ", StringComparison.Ordinal);
        }

        // A stripped word can leave one letter behind — "all-stars" minus "all-star" — and digits are
        // age brackets. Two letters or more is a word, and a word here names another contest.
        return !name
            .Split([' ', '(', ')', '/', '&', '-', ',', '.'], StringSplitOptions.RemoveEmptyEntries)
            .Any(word => word.Count(char.IsLetter) > 1);
    }

    public static RoundKind KindOf(string roundName)
    {
        var name = roundName.ToLowerInvariant();

        // "quarter" must be checked before "final" — "quarterfinal" contains "final" as a substring
        // and would otherwise be counted as one.
        return name.Contains("prelim") ? RoundKind.Prelim
            : name.Contains("quarter") ? RoundKind.Quarter
            : name.Contains("semi") ? RoundKind.Semi
            : name.Contains("final") ? RoundKind.Final
            : RoundKind.Other;
    }

    private static HtmlDocument Load(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        return doc;
    }

    /// <summary>
    /// A string or a number as text. Nothing guarantees which of the two scoring.dance sends for an id,
    /// a bib or a name, and <see cref="JsonElement.GetString"/> throws on a number.
    /// </summary>
    private static string? Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        _ => null,
    };

    private static string Clean(string? text) =>
        HtmlEntity.DeEntitize(text ?? string.Empty).Replace(' ', ' ').Trim();

    [GeneratedRegex(@"/events/(\d+)/results/""")]
    private static partial Regex EventHrefRegex();

    [GeneratedRegex(@"/wsdc/registry/(\d+)\.html$")]
    private static partial Regex WallRegistryLinkRegex();
}
