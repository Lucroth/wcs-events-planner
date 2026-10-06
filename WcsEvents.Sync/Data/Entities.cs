namespace WcsEvents.Sync.Data;

public enum Role
{
    Leader = 0,
    Follower = 1,
}

public class Dancer
{
    public int Wscid { get; set; }
    public required string FirstName { get; set; }
    public required string LastName { get; set; }
    public DateTime? LastCrawledUtc { get; set; }

    public List<Placement> Placements { get; set; } = [];
}

public class Division
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string Abbreviation { get; set; }

    /// <summary>Ladder position for advancement comparisons; null for divisions outside the main ladder (Masters, Juniors, ...).</summary>
    public int? SortOrder { get; set; }
}

/// <summary>
/// One competition result. The registry reuses a single event id for every running of an event and
/// reports the name and location as they stood that year, so both live here rather than in an
/// event table: (EventId, DateRaw) identifies the running that this result belongs to.
/// A dancer can hold several results for one running — separate contests the registry does not
/// name — so rows carry a surrogate key rather than a natural one.
/// </summary>
public class Placement
{
    public int Id { get; set; }

    public int DancerWscid { get; set; }
    public int EventId { get; set; }
    public int DivisionId { get; set; }
    public Role Role { get; set; }

    /// <summary>Original "August 2018" string; part of the key, since it identifies which running of the event this was.</summary>
    public required string DateRaw { get; set; }

    public required string EventName { get; set; }
    public string? EventLocation { get; set; }

    /// <summary>DateRaw parsed to the first of the month; null when unparseable.</summary>
    public DateOnly? Date { get; set; }

    public int Points { get; set; }

    /// <summary>Registry result string: "1".."5" for placements, "F" for a finalist without a top-5 finish.</summary>
    public required string ResultRaw { get; set; }

    /// <summary>Numeric finish 1-5; null for finalists.</summary>
    public int? Rank { get; set; }

    public Dancer Dancer { get; set; } = null!;
    public Division Division { get; set; } = null!;
}

public enum RoundKind
{
    Prelim = 0,
    Semi = 1,
    Final = 2,
    Other = 3,

    /// <summary>Added after Other — the numeric values are stored as-is, so existing rows must not shift.</summary>
    Quarter = 4,
}

/// <summary>
/// One round of one competition as published by scoring.dance. Unlike the WSDC registry, prelim
/// rounds list the whole field, which is what makes a real percentile possible.
/// </summary>
public class ScoringRound
{
    /// <summary>scoring.dance round id.</summary>
    public int Id { get; set; }

    public int ScoringEventId { get; set; }
    public required string EventName { get; set; }
    public DateOnly? EventDate { get; set; }
    public string? Location { get; set; }
    public string? Country { get; set; }

    /// <summary>Round label as published, e.g. "Novice Jack&amp;Jill prelim".</summary>
    public required string RoundName { get; set; }

    /// <summary>WSDC division abbreviation parsed from the round name; null when it maps to none.</summary>
    public string? DivisionAbbreviation { get; set; }

    /// <summary>
    /// The division's Jack &amp; Jill rather than a Strictly, a routine or another format. Only these
    /// are comparable to each other, so a division's numbers are built from them alone.
    /// </summary>
    public bool IsJackAndJill { get; set; }

    public RoundKind Kind { get; set; }

    public List<ScoringEntry> Entries { get; set; } = [];
}

/// <summary>
/// One dancer in one round. Couples are stored as two entries sharing a position, so counting
/// entries of a role gives the size of that side of the field.
/// </summary>
public class ScoringEntry
{
    public int Id { get; set; }
    public int RoundId { get; set; }

    /// <summary>Null when scoring.dance has no registry link for the dancer.</summary>
    public int? Wscid { get; set; }

    public required string Name { get; set; }
    public string? Bib { get; set; }

    /// <summary>Null until inferred; scoring.dance does not label the two prelim tables.</summary>
    public Role? Role { get; set; }

    /// <summary>Which of the round's tables this row came from — the two sides of a Jack &amp; Jill.</summary>
    public int TableIndex { get; set; }

    /// <summary>Standing within the table as published: the placement in a final, the score order in a prelim.</summary>
    public int Position { get; set; }

    public string? Score { get; set; }
    public bool Advanced { get; set; }
    public bool IsAlternate { get; set; }
    public bool IsScratched { get; set; }

    /// <summary>True when the WSDC id was recovered by bib or name, not published by scoring.dance.</summary>
    public bool LinkedByMatch { get; set; }

    public ScoringRound Round { get; set; } = null!;
    public List<JudgeMark> Marks { get; set; } = [];
}

/// <summary>
/// What one judge gave one dancer. Rounds before the final are scored with callback marks worth
/// points; a final is scored by each judge placing every couple.
/// </summary>
public class JudgeMark
{
    public int Id { get; set; }
    public int EntryId { get; set; }

    public required string Judge { get; set; }

    /// <summary>As published: "Yes", "No", "Alt1" in a callback round, or the placement in a final.</summary>
    public required string Mark { get; set; }

    /// <summary>Points the mark carries in a callback round; null in a final.</summary>
    public double? Points { get; set; }

    /// <summary>The placement this judge gave in a final; null in a callback round.</summary>
    public int? Placement { get; set; }

    public ScoringEntry Entry { get; set; } = null!;
}

public class CrawlState
{
    public int Id { get; set; }
    public int LastAttemptedWscid { get; set; }
    public int HighestFoundWscid { get; set; }
    public DateTime? LastFullCrawlCompletedUtc { get; set; }

    /// <summary>When a scoring.dance sync last walked every event, so the weekly one is timed from it.</summary>
    public DateTime? LastScoringSyncUtc { get; set; }

}

/// <summary>
/// An event as scoring.dance knows it, past or upcoming. Unlike <see cref="Placement"/>, whose event
/// id is reused by every running of a series, a scoring.dance id is one running: "Bavarian Open 2026"
/// and "Bavarian Open 2025" are separate ids.
/// </summary>
public class ScoringEvent
{
    /// <summary>scoring.dance event id.</summary>
    public int Id { get; set; }

    public required string Name { get; set; }

    public DateOnly? DateFrom { get; set; }
    public DateOnly? DateTo { get; set; }

    public string? City { get; set; }

    /// <summary>Country name as scoring.dance spells it; ISO3 codes from the listing are mapped first.</summary>
    public string? Country { get; set; }

    /// <summary>Awards WSDC points.</summary>
    public bool IsWsdc { get; set; }

    /// <summary>Registration page, when the listing carries one.</summary>
    public string? TicketUrl { get; set; }

}

/// <summary>
/// What was last written to each Firestore document. The free tier allows 20k writes a day, so a
/// document is only written again when its content changed.
/// </summary>
public class PublishedDoc
{
    public required string Path { get; set; }
    public required string Hash { get; set; }
    public DateTime PublishedUtc { get; set; }
}
