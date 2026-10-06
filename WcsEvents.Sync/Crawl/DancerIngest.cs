using Microsoft.EntityFrameworkCore;
using WcsEvents.Sync.Data;
using WcsEvents.Sync.Wsdc;

namespace WcsEvents.Sync.Crawl;

/// <summary>
/// Writes registry lookup responses into the local mirror. Holds the set of known division ids in
/// memory so ingesting a dancer needs no lookup queries.
/// </summary>
public sealed class DancerIngest(AppDbContext db)
{
    public const string WestCoastSwing = "West Coast Swing";

    private HashSet<int>? knownDivisionIds;

    /// <summary>
    /// Upserts the dancer and replaces their placements, adding any division the registry reports
    /// that the seed did not know about. Saves before returning, in one transaction, so a failed
    /// save cannot leave a dancer with their old placements deleted and no new ones written.
    /// </summary>
    public async Task IngestAsync(DancerResponseDto dto, CancellationToken ct)
    {
        knownDivisionIds ??= [.. await db.Divisions.Select(d => d.Id).ToListAsync(ct)];

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var dancer = await db.Dancers.FindAsync([dto.Dancer.Wscid], ct);
        if (dancer is null)
        {
            dancer = new Dancer { Wscid = dto.Dancer.Wscid, FirstName = dto.Dancer.FirstName, LastName = dto.Dancer.LastName };
            db.Dancers.Add(dancer);
        }
        else
        {
            dancer.FirstName = dto.Dancer.FirstName;
            dancer.LastName = dto.Dancer.LastName;

            // WSDC corrects results retroactively, so a re-crawled dancer's placements are replaced wholesale.
            await db.Placements.Where(p => p.DancerWscid == dto.Dancer.Wscid).ExecuteDeleteAsync(ct);
        }

        dancer.LastCrawledUtc = DateTime.UtcNow;

        AddPlacements(dto, dancer.Wscid);

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        db.ChangeTracker.Clear();
    }

    private void AddPlacements(DancerResponseDto dto, int wscid)
    {
        if (dto.Placements is null || !dto.Placements.TryGetValue(WestCoastSwing, out var divisions))
        {
            return;
        }

        foreach (var block in divisions.Values)
        {
            if (knownDivisionIds!.Add(block.Division.Id))
            {
                // Unknown divisions get no ladder position, which excludes them from advancement comparisons.
                db.Divisions.Add(new Division
                {
                    Id = block.Division.Id,
                    Name = block.Division.Name,
                    Abbreviation = block.Division.Abbreviation,
                    SortOrder = null,
                });
            }

            foreach (var competition in block.Competitions)
            {
                db.Placements.Add(new Placement
                {
                    DancerWscid = wscid,
                    EventId = competition.Event.Id,
                    DivisionId = block.Division.Id,
                    Role = ParseRole(competition.Role),
                    DateRaw = competition.Event.Date,
                    Date = MonthYear.Parse(competition.Event.Date),
                    EventName = competition.Event.Name,
                    EventLocation = competition.Event.Location,
                    Points = competition.Points,
                    ResultRaw = competition.Result,
                    Rank = int.TryParse(competition.Result, out var rank) ? rank : null,
                });
            }
        }
    }

    private static Role ParseRole(string role) =>
        role.Equals("leader", StringComparison.OrdinalIgnoreCase) ? Role.Leader : Role.Follower;
}
