
using Microsoft.EntityFrameworkCore;
using WcsEvents.Sync.Data;
using WcsEvents.Sync.Metrics;

namespace WcsEvents.Sync.Scoring;

/// <summary>Turns parsed rounds into rows, and works out which prelim table is which role.</summary>
public static class ScoringStore
{
    public static ScoringRound ToEntity(int scoringEventId, int roundId, string fallbackName, ParsedRound parsed)
    {
        var roundName = string.IsNullOrEmpty(parsed.RoundName) ? fallbackName : parsed.RoundName;

        return new ScoringRound
        {
            Id = roundId,
            ScoringEventId = scoringEventId,
            EventName = parsed.EventName,
            EventDate = parsed.EventDate,
            Location = parsed.Location,
            Country = parsed.Country,
            RoundName = roundName,
            DivisionAbbreviation = ScoringParser.DivisionOf(roundName),
            IsJackAndJill = ScoringParser.IsJackAndJill(roundName),
            Kind = ScoringParser.KindOf(roundName),
            Entries =
            [
                .. parsed.Entries.Select(e => new ScoringEntry
                {
                    Wscid = e.Wscid,
                    Name = e.Name,
                    Bib = e.Bib,
                    Role = e.Role,
                    TableIndex = e.TableIndex,
                    Position = e.Position,
                    Score = e.Score,
                    Advanced = e.Advanced,
                    IsAlternate = e.IsAlternate,
                    IsScratched = e.IsScratched,
                    Marks =
                    [
                        .. e.Marks.Select(m => new JudgeMark
                        {
                            Judge = m.Judge,
                            Mark = m.Mark,
                            Points = m.Points,
                            Placement = m.Placement,
                        }),
                    ],
                }),
            ],
        };
    }

    /// <summary>
    /// Re-reads the contest format, stage and division of every stored round. Each is a reading of
    /// the round name that will keep being sharpened as events invent new ones, so all three are
    /// applied to the whole mirror rather than only to rounds arriving from here on — a division the
    /// classifier learns today reaches the rounds mirrored last year. Returns rows changed.
    /// </summary>
    public static async Task<int> ClassifyRoundsAsync(AppDbContext db, CancellationToken ct)
    {
        var rounds = await db.ScoringRounds.ToListAsync(ct);

        foreach (var round in rounds)
        {
            round.IsJackAndJill = ScoringParser.IsJackAndJill(round.RoundName);
            round.Kind = ScoringParser.KindOf(round.RoundName);
            round.DivisionAbbreviation = ScoringParser.DivisionOf(round.RoundName);
        }

        return await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Contest formats where dancing the other role is the point, so the registry disagreeing with
    /// the published role is expected rather than suspicious.
    /// </summary>
    private static readonly string[] RoleSwapFormats = ["switch", "all-american", "all american", "reverse"];

    /// <summary>A registry role only counts as evidence once a dancer has danced it this often.</summary>
    private const int MinPlacementsForRoleEvidence = 3;

    /// <summary>A published table is only judged once this many of its dancers are known to the registry.</summary>
    private const int MinKnownDancersToJudge = 4;

    /// <summary>
    /// A final states each couple's roles outright, and those roles are what the prelim tables of the
    /// same event are labelled from. Organisers do sometimes enter a whole final with the two columns
    /// swapped — 39 of 1,481 division finals in this mirror, All-Stars finals above all, and usually
    /// unanimously: every leader filed as a follower and every follower as a leader. Left alone the
    /// error spreads, because the bibs carry the wrong role into that event's prelims at a weight the
    /// registry cannot outvote.
    /// <para>
    /// Where the registry knows enough of a published table's dancers and contradicts the published
    /// role for nearly all of them, the table is flipped, and the same event's inferred tables for
    /// that division are unlabelled so the next inference reads them from corrected evidence. Only
    /// ordinary division Jack &amp; Jills are judged: in a Switch or All-American contest the
    /// contradiction is deliberate. Returns tables flipped.
    /// </para>
    /// </summary>
    public static async Task<int> RepairSwappedRolesAsync(AppDbContext db, CancellationToken ct)
    {
        var registryRole = await DominantRolesAsync(db, MinPlacementsForRoleEvidence, ct);

        var published = await db.ScoringEntries
            .Where(e => e.Role != null && e.Round.IsJackAndJill && e.Round.DivisionAbbreviation != null)
            .Select(e => new
            {
                Entry = e, e.Round.ScoringEventId, e.Round.DivisionAbbreviation, e.Round.RoundName, e.Round.Kind,
            })
            .ToListAsync(ct);

        var flipped = 0;
        HashSet<(int Event, string Division)> affected = [];

        foreach (var table in published.GroupBy(r => (r.Entry.RoundId, r.Entry.TableIndex)))
        {
            ct.ThrowIfCancellationRequested();

            // Only a table that states both roles was read from the couple columns; a one-role table
            // was labelled by inference and is corrected through it, not here.
            if (table.Select(r => r.Entry.Role).Distinct().Count() < 2
                || RoleSwapFormats.Any(f => table.First().RoundName.Contains(f, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var agree = 0;
            var disagree = 0;

            foreach (var row in table)
            {
                if (row.Entry.Wscid is { } wscid && registryRole.TryGetValue(wscid, out var usual))
                {
                    if (usual == row.Entry.Role) agree++; else disagree++;
                }
            }

            if (agree + disagree < MinKnownDancersToJudge || disagree <= 2 * agree)
            {
                continue;
            }

            foreach (var row in table)
            {
                row.Entry.Role = row.Entry.Role == Role.Leader ? Role.Follower : Role.Leader;
            }

            flipped++;
            affected.Add((table.First().ScoringEventId, table.First().DivisionAbbreviation!));
        }

        // The inferred tables of an affected division were labelled from the evidence just corrected,
        // so they are sent back through inference rather than trusted.
        foreach (var (eventId, division) in affected)
        {
            var inferred = published
                .Where(r => r.ScoringEventId == eventId && r.DivisionAbbreviation == division && r.Kind != RoundKind.Final)
                .GroupBy(r => (r.Entry.RoundId, r.Entry.TableIndex))
                .Where(g => g.Select(r => r.Entry.Role).Distinct().Count() == 1);

            foreach (var row in inferred.SelectMany(g => g))
            {
                row.Entry.Role = null;
            }
        }

        await db.SaveChangesAsync(ct);

        return flipped;
    }

    /// <summary>
    /// A Jack &amp; Jill prelim usually publishes two unlabelled tables, one per role. Rounds that pair
    /// a leader with a follower on one row state both roles outright, so a bib seen in such a round
    /// settles that dancer's role for the whole event. Tables with no bib evidence fall back to how
    /// their dancers usually compete. Returns rows labelled.
    /// <para>
    /// Every one-role table is labelled afresh on each run, not only unlabelled ones: the parser
    /// never labels a prelim table, so any such label is an earlier run's inference, and evidence
    /// improves as the mirror grows (or, as with SwingVester 2025/26, a bad source of it is fixed).
    /// </para>
    /// <para>
    /// Finals are skipped: their roles come straight from the couple columns of the result list. The
    /// only role-less final entry is the deliberate one the parser leaves when scoring.dance named a
    /// dancer as both halves of a couple — its role genuinely is not known, and guessing one here
    /// would be a guess at exactly the thing that could not be read.
    /// </para>
    /// </summary>
    public static async Task<int> InferRolesAsync(AppDbContext db, CancellationToken ct)
    {
        var roleByDancer = await DominantRolesAsync(db, minCount: 1, ct);
        var roleByBib = await PublishedRolesByBibAsync(db, roleByDancer, ct);

        var rows = await db.ScoringEntries
            .Where(e => e.Round.Kind != RoundKind.Final)
            .Select(e => new { Entry = e, e.Round.ScoringEventId })
            .ToListAsync(ct);

        var labelled = 0;

        // A table already holding both roles was read from leader/follower columns and is left alone.
        foreach (var table in rows.GroupBy(r => (r.Entry.RoundId, r.Entry.TableIndex))
                     .Where(t => t.Select(r => r.Entry.Role).Where(r => r is not null).Distinct().Count() < 2))
        {
            ct.ThrowIfCancellationRequested();

            List<(ScoringEntry Entry, Role? Evidence, int Weight)> evidence = [.. table.Select(row =>
                row.Entry.Bib is not null && roleByBib.TryGetValue((row.ScoringEventId, row.Entry.Bib), out var byBib)
                    ? (row.Entry, (Role?)byBib, 100)
                    : row.Entry.Wscid is { } wscid && roleByDancer.TryGetValue(wscid, out var byHistory)
                        ? (row.Entry, byHistory, 1)
                        : (row.Entry, (Role?)null, 0))];

            var leaders = evidence.Where(e => e.Evidence is Role.Leader).Sum(e => e.Weight);
            var followers = evidence.Where(e => e.Evidence is Role.Follower).Sum(e => e.Weight);
            Role? tableRole = leaders == followers ? null : leaders > followers ? Role.Leader : Role.Follower;
            foreach (var row in table.Where(r => r.Entry.Role != tableRole))
            {
                // No evidence either way leaves the rows unlabelled rather than guessed.
                row.Entry.Role = tableRole;
                labelled++;
            }
        }

        await db.SaveChangesAsync(ct);

        return labelled;
    }

    /// <summary>
    /// Bib to role, per event, from Jack &amp; Jill rounds that publish the pairing itself. A table
    /// holding both roles was read from leader/follower columns; a table holding one role was
    /// labelled by inference, and treating that as evidence would let a single guess spread.
    /// <para>
    /// Only Jack &amp; Jill: Pro-Am "(Am F)" rounds print the amateur follower in the leader column,
    /// and some events list Strictly couples in no particular order (SwingVester 2025/26 put its
    /// Novice followers there, which then labelled the whole Novice follower prelim as leaders). A
    /// pairing table whose columns mostly disagree with the dancers' registry roles is dropped too.
    /// </para>
    /// </summary>
    private static async Task<Dictionary<(int Event, string Bib), Role>> PublishedRolesByBibAsync(
        AppDbContext db, IReadOnlyDictionary<int, Role> roleByDancer, CancellationToken ct)
    {
        var rows = await db.ScoringEntries.AsNoTracking()
            .Where(e => e.Role != null && e.Bib != null && e.Round.IsJackAndJill)
            .Select(e => new { e.Round.ScoringEventId, e.RoundId, e.TableIndex, e.Bib, e.Role, e.Wscid })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => (r.RoundId, r.TableIndex))
            .Where(g => g.Select(r => r.Role).Distinct().Count() > 1 && ColumnsTrusted([.. g.Select(r => (r.Role!.Value, r.Wscid))], roleByDancer))
            .SelectMany(g => g)
            .GroupBy(r => (r.ScoringEventId, r.Bib!))
            .Where(g => g.Select(r => r.Role).Distinct().Count() == 1)
            .ToDictionary(g => g.Key, g => g.First().Role!.Value);
    }

    /// <summary>Whether a pairing table's columns agree with its dancers' usual roles: at least 80%
    /// of those with a registry history, or too few of them to judge.</summary>
    internal static bool ColumnsTrusted(IReadOnlyList<(Role Role, int? Wscid)> rows, IReadOnlyDictionary<int, Role> roleByDancer)
    {
        List<bool> agree = [.. rows
            .Where(r => r.Wscid is { } id && roleByDancer.ContainsKey(id))
            .Select(r => roleByDancer[r.Wscid!.Value] == r.Role)];

        return agree.Count < 4 || agree.Count(a => a) >= 0.8 * agree.Count;
    }

    /// <summary>
    /// scoring.dance links most, but not all, rows to a WSDC id. Two ways to recover the rest: a bib
    /// already identified elsewhere in the same event, which is reliable, and a full name matching
    /// exactly one registry dancer, which is a guess and so is confined to one event's bib.
    /// Returns rows linked.
    /// </summary>
    public static async Task<int> LinkEntriesAsync(AppDbContext db, CancellationToken ct)
    {
        await DropMismatchedIdsAsync(db, ct);

        var linked = await LinkByBibAsync(db, ct);
        linked += await LinkByNameWithinEventAsync(db, ct);
        linked += await LinkByNameAsync(db, ct);

        return linked;
    }

    /// <summary>
    /// Attaches an id to rows the site left anonymous where the same name is already identified
    /// elsewhere in that event. A Strictly prints one bib for the couple, so the follower's half
    /// carries no bib of its own and nothing else can reach it — but if she danced the Jack &amp; Jill
    /// under her registry id, the same name at the same event is her. Returns rows linked.
    /// </summary>
    private static async Task<int> LinkByNameWithinEventAsync(AppDbContext db, CancellationToken ct)
    {
        // Seed only from ids scoring.dance published, for the same reason the bib map does: a name
        // recovered by an earlier rule is a guess, and seeding from guesses spreads one mistake
        // across the event and compounds it on every later run.
        var identified = await db.ScoringEntries.AsNoTracking()
            .Where(e => e.Wscid != null && !e.LinkedByMatch)
            .Select(e => new { e.Round.ScoringEventId, e.Name, e.Wscid })
            .Distinct()
            .ToListAsync(ct);

        // Only when every identified row of that name in the event agrees on one dancer; two people
        // of the same name at one event leaves nothing to choose between them.
        var byName = identified
            .GroupBy(x => (x.ScoringEventId, Name: Names.Normalize(x.Name)))
            .Where(g => g.Select(x => x.Wscid).Distinct().Count() == 1)
            .ToDictionary(g => g.Key, g => g.First().Wscid!.Value);

        if (byName.Count is 0)
        {
            return 0;
        }

        var unlinked = await db.ScoringEntries
            .Where(e => e.Wscid == null)
            .Select(e => new { Entry = e, e.Round.ScoringEventId })
            .ToListAsync(ct);

        var linked = 0;

        foreach (var row in unlinked)
        {
            if (byName.TryGetValue((row.ScoringEventId, Names.Normalize(row.Entry.Name)), out var wscid))
            {
                row.Entry.Wscid = wscid;
                row.Entry.LinkedByMatch = true;
                linked++;
            }
        }

        await db.SaveChangesAsync(ct);

        return linked;
    }

    /// <summary>
    /// Removes published ids that cannot belong to the dancer named beside them. scoring.dance emits
    /// a few placeholder ids that land on real registry numbers — one carries five different
    /// people's names — and every later rule treats a published id as ground truth. An id is kept
    /// when the row's name shares a word with the registry dancer's, which tolerates reversed names,
    /// casing and middle initials. Returns ids dropped.
    /// </summary>
    private static async Task<int> DropMismatchedIdsAsync(AppDbContext db, CancellationToken ct)
    {
        var suspect = await db.ScoringEntries.AsNoTracking()
            .Where(e => e.Wscid != null && !e.LinkedByMatch)
            .Select(e => new { e.Wscid, e.Name })
            .Distinct()
            .ToListAsync(ct);

        if (suspect.Count is 0)
        {
            return 0;
        }

        var ids = suspect.Select(s => s.Wscid!.Value).Distinct().ToList();

        var registry = await db.Dancers.AsNoTracking()
            .Where(d => ids.Contains(d.Wscid))
            .Select(d => new { d.Wscid, Full = d.FirstName + " " + d.LastName })
            .ToDictionaryAsync(d => d.Wscid, d => d.Full, ct);

        List<(int Wscid, string Name)> mismatched = [];

        foreach (var row in suspect)
        {
            // An id the mirror has never heard of cannot be checked, and neither can a name with no
            // word long enough to compare. Both are left alone rather than guessed at.
            if (registry.TryGetValue(row.Wscid!.Value, out var registered)
                && Names.Comparable(row.Name, registered)
                && !Names.SharesAWord(row.Name, registered))
            {
                mismatched.Add((row.Wscid!.Value, row.Name));
            }
        }

        if (mismatched.Count is 0)
        {
            return 0;
        }

        // The rows to clear are found by (id, name) pair, which no single WHERE can express without
        // also clearing the pairs that matched. One read of the handful of ids involved, then one
        // update over the rows it found.
        HashSet<(int, string)> drop = [.. mismatched];
        var doomed = mismatched.Select(m => m.Wscid).Distinct().ToList();

        var rows = await db.ScoringEntries.AsNoTracking()
            .Where(e => e.Wscid != null && !e.LinkedByMatch && doomed.Contains(e.Wscid!.Value))
            .Select(e => new { e.Id, Wscid = e.Wscid!.Value, e.Name })
            .ToListAsync(ct);

        var entryIds = rows.Where(r => drop.Contains((r.Wscid, r.Name))).Select(r => r.Id).ToList();

        await db.ScoringEntries
            .Where(e => entryIds.Contains(e.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Wscid, (int?)null), ct);

        return mismatched.Count;
    }


    private static async Task<int> LinkByBibAsync(AppDbContext db, CancellationToken ct)
    {
        // Seed only from ids scoring.dance published. Ids recovered by name are guesses, and letting
        // them seed this map would spread one bad guess across a whole event.
        var identified = await db.ScoringEntries.AsNoTracking()
            .Where(e => e.Wscid != null && e.Bib != null && !e.LinkedByMatch)
            .Select(e => new { e.Round.ScoringEventId, e.Bib, e.Wscid })
            .Distinct()
            .ToListAsync(ct);

        var byBib = identified
            .GroupBy(x => (x.ScoringEventId, x.Bib!))
            .Where(g => g.Select(x => x.Wscid).Distinct().Count() == 1)
            .ToDictionary(g => g.Key, g => g.First().Wscid!.Value);

        if (byBib.Count is 0)
        {
            return 0;
        }

        var unlinked = await db.ScoringEntries
            .Where(e => e.Wscid == null && e.Bib != null)
            .Select(e => new { Entry = e, e.Round.ScoringEventId })
            .ToListAsync(ct);

        var linked = 0;

        foreach (var row in unlinked)
        {
            if (byBib.TryGetValue((row.ScoringEventId, row.Entry.Bib!), out var wscid))
            {
                row.Entry.Wscid = wscid;
                row.Entry.LinkedByMatch = true;
                linked++;
            }
        }

        await db.SaveChangesAsync(ct);

        return linked;
    }

    private static async Task<int> LinkByNameAsync(AppDbContext db, CancellationToken ct)
    {
        var unlinked = await db.ScoringEntries
            .Where(e => e.Wscid == null && e.Bib != null)
            .Select(e => new { Entry = e, e.Round.ScoringEventId })
            .ToListAsync(ct);

        if (unlinked.Count is 0)
        {
            return 0;
        }

        // Only a name held by exactly one registry dancer is a candidate at all.
        var candidates = await db.Dancers.AsNoTracking()
            .Select(d => new { d.Wscid, FullName = d.FirstName + " " + d.LastName })
            .ToListAsync(ct);

        var unique = candidates
            .GroupBy(d => Names.Normalize(d.FullName))
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.First().Wscid, StringComparer.Ordinal);

        var linked = 0;

        foreach (var perEvent in unlinked.GroupBy(r => r.ScoringEventId))
        {
            ct.ThrowIfCancellationRequested();

            // Within one event a name must belong to exactly one bib. Two bibs means two people of
            // that name danced it, and nothing here says which one holds the registry entry.
            var bibsByName = perEvent
                .GroupBy(r => Names.Normalize(r.Entry.Name))
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(r => r.Entry.Bib!).Distinct(StringComparer.Ordinal).Count(),
                    StringComparer.Ordinal);

            foreach (var row in perEvent)
            {
                var key = Names.Normalize(row.Entry.Name);

                if (unique.TryGetValue(key, out var wscid) && bibsByName[key] is 1)
                {
                    row.Entry.Wscid = wscid;
                    row.Entry.LinkedByMatch = true;
                    linked++;
                }
            }
        }

        await db.SaveChangesAsync(ct);

        return linked;
    }

    /// <summary>
    /// The role each dancer has competed in most often in the registry, for dancers whose most
    /// frequent role holds at least <paramref name="minCount"/> placements.
    /// </summary>
    private static async Task<Dictionary<int, Role>> DominantRolesAsync(AppDbContext db, int minCount, CancellationToken ct)
    {
        var counts = await db.Placements.AsNoTracking()
            .GroupBy(p => new { p.DancerWscid, p.Role })
            .Select(g => new { g.Key.DancerWscid, g.Key.Role, Count = g.Count() })
            .ToListAsync(ct);

        return counts
            .GroupBy(x => x.DancerWscid)
            .Select(g => g.OrderByDescending(x => x.Count).First())
            .Where(x => x.Count >= minCount)
            .ToDictionary(x => x.DancerWscid, x => x.Role);
    }

    /// <summary>
    /// Records every event the mirror knows of. The listing covers what has not happened yet; events
    /// that have already run are recovered from their own rounds, since the site drops them from the
    /// listing once they are over. Listing values win where the two disagree, being the event's own.
    /// Returns rows written.
    /// </summary>
    public static async Task<int> SyncEventsAsync(
        AppDbContext db,
        IReadOnlyList<ParsedEvent> listed,
        CancellationToken ct)
    {
        var existing = await db.ScoringEvents.ToDictionaryAsync(e => e.Id, ct);

        var fromRounds = await db.ScoringRounds.AsNoTracking()
            .GroupBy(r => r.ScoringEventId)
            .Select(g => new
            {
                Id = g.Key,
                Name = g.Max(r => r.EventName),
                From = g.Min(r => r.EventDate),
                To = g.Max(r => r.EventDate),
                City = g.Max(r => r.Location),
                Country = g.Max(r => r.Country),
            })
            .ToListAsync(ct);

        var written = 0;

        foreach (var round in fromRounds)
        {
            var row = Row(round.Id);

            // An event with published results has held its rounds; the listing, if it also carries
            // this event, corrects the dates below.
            if (string.IsNullOrEmpty(row.Name))
            {
                // Max over a required column, so null only if the group somehow held none.
                row.Name = round.Name ?? string.Empty;
            }

            row.DateFrom ??= round.From;
            row.DateTo ??= round.To;
            row.City ??= round.City;
            row.Country ??= Countries.Name(round.Country);
            written++;
        }

        foreach (var listing in listed)
        {
            var row = Row(listing.Id);

            row.Name = listing.Name;
            row.DateFrom = listing.DateFrom ?? row.DateFrom;
            row.DateTo = listing.DateTo ?? listing.DateFrom ?? row.DateTo;
            row.City = listing.City ?? row.City;
            row.Country = Countries.Name(listing.CountryCode) ?? row.Country;
            row.IsWsdc = listing.IsWsdc;
            row.TicketUrl = listing.TicketUrl ?? row.TicketUrl;
            written++;
        }

        InheritCities(existing.Values);

        await db.SaveChangesAsync(ct);

        return written;

        ScoringEvent Row(int id)
        {
            if (existing.TryGetValue(id, out var found))
            {
                return found;
            }

            var created = new ScoringEvent { Id = id, Name = string.Empty };
            existing[id] = created;
            db.ScoringEvents.Add(created);

            return created;
        }
    }

    /// <summary>
    /// An upcoming edition is often listed before its organisers fill in the city, or with a
    /// placeholder: "Rome" is the listing's default, and others are copied from another event
    /// ("Bron" for a Swedish one). A city some other event places in a different country is suspect;
    /// a suspect city, or a missing one, is replaced by the city of the latest earlier edition, since
    /// the series rarely moves. A default "Rome" with no earlier edition to fall back on is dropped.
    /// </summary>
    internal static void InheritCities(IEnumerable<ScoringEvent> events)
    {
        List<ScoringEvent> all = [.. events.Where(e => e.DateFrom is not null)];

        var byCity = all
            .Where(e => e.City is not null && e.Country is not null)
            .ToLookup(e => e.City!.Trim(), StringComparer.OrdinalIgnoreCase);

        foreach (var e in all)
        {
            var isDefault = string.Equals(e.City?.Trim(), "Rome", StringComparison.OrdinalIgnoreCase) && e.Country != "Italy";
            var isSuspect = isDefault
                || (e.City is not null && e.Country is not null && byCity[e.City.Trim()].Any(o => o.Id != e.Id && o.Country != e.Country));

            if (e.City is not null && !isSuspect)
            {
                continue;
            }

            var previous = all
                .Where(p => p.City is not null && p.DateFrom < e.DateFrom && EventMatching.SameName(p.Name, e.Name)
                    && (e.Country is null || p.Country == e.Country))
                .MaxBy(p => p.DateFrom);

            if (previous is not null)
            {
                e.City = previous.City;
                e.Country ??= previous.Country;
            }
            else if (isDefault)
            {
                e.City = null;
            }
        }
    }
}
