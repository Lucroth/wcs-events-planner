using Microsoft.EntityFrameworkCore;
using WcsEvents.Sync.Data;

namespace WcsEvents.Sync.Scoring;

/// <summary>
/// Mirrors scoring.dance rounds. Prelims carry the whole field, which the WSDC registry never
/// publishes, so these rows are what turn a percentile-of-finalists into a real one.
/// </summary>
public sealed partial class ScoringSync(
    AppDbContext db,
    ScoringClient client,
    ILogger<ScoringSync> logger)
{
    /// <summary>How long linking and role inference may run once fetching has stopped.</summary>
    private static readonly TimeSpan FinishingBudget = TimeSpan.FromMinutes(2);

    /// <summary>How far past the newest known event id to probe for events not yet announced.</summary>
    private const int IdProbeMargin = 20;

    /// <summary>
    /// How long after an event its results are taken as final. An event already mirrored and older
    /// than this is not fetched again: its pages cost a request each and no longer change.
    /// </summary>
    private static readonly TimeSpan SettledAfter = TimeSpan.FromDays(60);

    public int RoundsAdded { get; private set; }

    public int EventsSeen { get; private set; }

    /// <summary>Fetches every event's rounds, skipping rounds already stored and events long settled.</summary>
    public async Task SyncAsync(CancellationToken ct)
    {
        var recent = await client.GetRecentAsync(ct);
        if (recent is null)
        {
            LogNoListing(logger);
            return;
        }

        // The listing is only a rolling window onto the newest events. Event ids are a dense counter,
        // so every id up to the newest is walked instead: that reaches the events the window has
        // already dropped, and the ones this mirror has never seen. Ids with nothing published cost
        // a single request each.
        var listed = ScoringParser.ParseEventIds(recent);
        var mirrored = await db.ScoringRounds.AsNoTracking()
            .GroupBy(r => r.ScoringEventId)
            .Select(g => new { Id = g.Key, Held = g.Max(r => r.EventDate) })
            .ToListAsync(ct);

        var settledBefore = DateOnly.FromDateTime(DateTime.UtcNow - SettledAfter);
        HashSet<int> settled = [.. mirrored.Where(e => e.Held < settledBefore).Select(e => e.Id)];

        var highest = Math.Max(listed.Count is 0 ? 0 : listed.Max(), mirrored.Count is 0 ? 0 : mirrored.Max(e => e.Id));
        var eventIds = Enumerable.Range(1, highest + IdProbeMargin)
            .Union(listed)
            .Union(mirrored.Select(e => e.Id))
            .Where(id => !settled.Contains(id))
            .Order()
            .ToList();

        var knownRounds = await db.ScoringRounds.AsNoTracking().Select(r => r.Id).ToListAsync(ct);
        HashSet<int> known = [.. knownRounds];

        LogSyncStarted(logger, eventIds.Count, settled.Count, known.Count);

        try
        {
            await FetchEventsAsync(eventIds, known, ct);
            await StampAsync(ct);
        }
        finally
        {
            // Rows stay invisible until their table has a role, so label whatever was fetched even
            // if the run was interrupted — but on a bounded budget, so shutdown is not held up.
            await FinishAsync();
        }
    }

    /// <summary>Records that a sync walked every event, creating the state row if nothing else has.</summary>
    private async Task StampAsync(CancellationToken ct)
    {
        var updated = await db.CrawlStates
            .Where(c => c.Id == 1)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.LastScoringSyncUtc, DateTime.UtcNow), ct);

        if (updated is 0)
        {
            db.CrawlStates.Add(new CrawlState { Id = 1, LastScoringSyncUtc = DateTime.UtcNow });
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }
    }

    /// <summary>
    /// Links, labels and records events for whatever the fetch stored. Never throws: it runs from a
    /// finally block, where an exception would hide whatever ended the fetch.
    /// </summary>
    private async Task FinishAsync()
    {
        using var finishing = new CancellationTokenSource(FinishingBudget);

        try
        {
            var linked = await ScoringStore.LinkEntriesAsync(db, finishing.Token);
            await ScoringStore.RepairSwappedRolesAsync(db, finishing.Token);
            var labelled = await ScoringStore.InferRolesAsync(db, finishing.Token);
            var events = await SyncEventsAsync(finishing.Token);

            LogEntriesLinked(logger, linked);
            LogRolesInferred(logger, labelled);
            LogEventsSynced(logger, events);
        }
        catch (Exception ex)
        {
            LogFinishingFailed(logger, ex);
        }

        LogSyncFinished(logger, EventsSeen, RoundsAdded);
    }

    /// <summary>
    /// Records the events themselves: the upcoming ones from the home listing, the past ones from the
    /// rounds already mirrored. Runs even when no round was added, since the listing alone carries upcoming events.
    /// </summary>
    public async Task<int> SyncEventsAsync(CancellationToken ct)
    {
        var home = await client.GetHomeAsync(ct);

        var listed = home is null ? [] : ScoringParser.ParseUpcomingEvents(home);
        if (listed.Count is 0)
        {
            LogNoUpcomingListing(logger);
        }

        return await ScoringStore.SyncEventsAsync(db, listed, ct);
    }

    private async Task FetchEventsAsync(IReadOnlyList<int> eventIds, HashSet<int> known, CancellationToken ct)
    {
        foreach (var eventId in eventIds)
        {
            ct.ThrowIfCancellationRequested();
            EventsSeen++;

            try
            {
                await FetchEventAsync(eventId, known, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // One event the site will not serve must not cost the remaining hundreds.
                LogEventFailed(logger, eventId, ex);
            }

            if (EventsSeen % 25 == 0)
            {
                LogSyncProgress(logger, EventsSeen, eventIds.Count, RoundsAdded);
            }
        }
    }

    private async Task FetchEventAsync(int eventId, HashSet<int> known, CancellationToken ct)
    {
        var eventPage = await client.GetEventResultsAsync(eventId, ct);
        if (eventPage is null)
        {
            return;
        }

        foreach (var link in ScoringParser.ParseRoundLinks(eventPage, eventId))
        {
            if (known.Contains(link.RoundId))
            {
                continue;
            }

            ct.ThrowIfCancellationRequested();

            try
            {
                var roundPage = await client.GetRoundAsync(eventId, link.RoundId, ct);
                var parsed = roundPage is null ? null : ScoringParser.ParseRound(roundPage);
                if (parsed is null || parsed.Entries.Count is 0)
                {
                    continue;
                }

                db.ScoringRounds.Add(ScoringStore.ToEntity(eventId, link.RoundId, link.Name, parsed));
                RoundsAdded++;

                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                LogRoundFailed(logger, link.RoundId, eventId, ex);
            }
            finally
            {
                known.Add(link.RoundId);
                db.ChangeTracker.Clear();
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "scoring.dance listing could not be read")]
    private static partial void LogNoListing(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "scoring.dance sync starting: {Events} events, {Settled} settled events skipped, {Known} rounds already stored")]
    private static partial void LogSyncStarted(ILogger logger, int events, int settled, int known);

    [LoggerMessage(Level = LogLevel.Information, Message = "scoring.dance sync at event {Seen}/{Total}, {Added} rounds added")]
    private static partial void LogSyncProgress(ILogger logger, int seen, int total, int added);

    [LoggerMessage(Level = LogLevel.Information, Message = "scoring.dance sync finished: {Events} events, {Added} rounds added")]
    private static partial void LogSyncFinished(ILogger logger, int events, int added);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Round {RoundId} of event {EventId} could not be read")]
    private static partial void LogRoundFailed(ILogger logger, int roundId, int eventId, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Event {EventId} could not be fetched; going on to the next one")]
    private static partial void LogEventFailed(ILogger logger, int eventId, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Linking, role inference or the event listing failed after the scoring.dance fetch")]
    private static partial void LogFinishingFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Linked {Rows} scoring.dance rows to a WSDC id by bib or name")]
    private static partial void LogEntriesLinked(ILogger logger, int rows);

    [LoggerMessage(Level = LogLevel.Information, Message = "Roles inferred for {Rows} scoring.dance rows")]
    private static partial void LogRolesInferred(ILogger logger, int rows);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Rows} events recorded")]
    private static partial void LogEventsSynced(ILogger logger, int rows);

    [LoggerMessage(Level = LogLevel.Warning, Message = "scoring.dance home page carried no upcoming events")]
    private static partial void LogNoUpcomingListing(ILogger logger);
}
