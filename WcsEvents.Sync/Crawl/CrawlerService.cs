using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WcsEvents.Sync.Data;
using WcsEvents.Sync.Scoring;
using WcsEvents.Sync.Wsdc;

namespace WcsEvents.Sync.Crawl;

public enum CrawlCommand
{
    /// <summary>Continue the id sweep from where it stopped, filling in dancers not yet mirrored.</summary>
    Sweep,

    /// <summary>Re-fetch already-mirrored dancers, oldest first, then sweep for newly registered ids.</summary>
    Refresh,

    /// <summary>Mirror scoring.dance rounds, which carry the full field the registry omits.</summary>
    Scoring,
}

/// <summary>
/// How far a sweep or refresh has got: ids or dancers done of <see cref="Total"/>, which is null when
/// nothing says how many lie ahead. A sweep's total runs to the highest id found so far; past it the
/// sweep is probing for new registrations and has no end it can name.
/// </summary>
public sealed record CrawlProgress(int Done, int? Total, DateTime StartedUtc)
{
    public double PerSecond => Done / Math.Max(1, (DateTime.UtcNow - StartedUtc).TotalSeconds);

    public TimeSpan? Eta => Total is { } total && total > Done && PerSecond > 0
        ? TimeSpan.FromSeconds((total - Done) / PerSecond)
        : null;
}

public sealed class CrawlOptions
{
    public double RequestsPerSecond { get; set; } = 3;

    /// <summary>Consecutive misses past the highest known id before a sweep concludes the registry has ended.</summary>
    public int MissStreakStop { get; set; } = 500;

    /// <summary>A refresh leaves out dancers crawled more recently than this, so one interrupted by a restart picks up where it stopped instead of starting the whole mirror over. Zero refreshes everyone.</summary>
    public TimeSpan RefreshSkipRecent { get; set; } = TimeSpan.FromDays(1);

    public string UserAgent { get; set; } = "wcs-events/1.0";
}

/// <summary>Mirrors the registry and scoring.dance into the local database, one command per run.</summary>
public sealed partial class CrawlerService(
    IServiceScopeFactory scopes,
    WsdcClient client,
    CrawlOptions options,
    ILogger<CrawlerService> logger)
{
    public CrawlProgress? Progress { get; private set; }

    /// <summary>
    /// Runs one command to the end, or until <paramref name="ct"/> fires. A sweep or refresh stopped
    /// early keeps its progress, so the next run carries on from there: a CI job has a time limit and
    /// a full registry sweep does not fit in one.
    /// </summary>
    public async Task RunAsync(CrawlCommand command, CancellationToken ct)
    {
        if (command is CrawlCommand.Scoring)
        {
            await SyncScoringAsync(ct);
            return;
        }

        try
        {
            if (command is CrawlCommand.Refresh)
            {
                await RefreshKnownDancersAsync(ct);
            }

            await SweepAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            LogStoppedEarly(logger, command);
        }

        // A sweep or refresh is the only thing that adds registry dancers, and the scoring linker
        // resolves a mirror row the moment its name maps to exactly one of them, so close that gap
        // here on the data just written. Not cancellable: it is short, and the time budget that may
        // have just run out was for fetching.
        await ReconcileScoringIdentitiesAsync(CancellationToken.None);
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ingest = new DancerIngest(db);

        var state = await GetStateAsync(db, ct);
        var wscid = state.LastAttemptedWscid;
        var start = wscid;
        var started = DateTime.UtcNow;
        int? ahead = state.HighestFoundWscid > start ? state.HighestFoundWscid - start : null;
        var missStreak = 0;

        Progress = new CrawlProgress(0, ahead, started);
        LogSweepStarted(logger, wscid + 1);

        try
        {
            while (!ct.IsCancellationRequested && missStreak < options.MissStreakStop)
            {
                wscid++;

                var dto = await FindReadableAsync(wscid, ct);
                if (dto is null)
                {
                    missStreak++;
                }
                else
                {
                    missStreak = 0;
                    await ingest.IngestAsync(dto, ct);
                    state.HighestFoundWscid = Math.Max(state.HighestFoundWscid, wscid);
                }

                state.LastAttemptedWscid = wscid;
                Progress = new CrawlProgress(wscid - start, ahead, started);

                if (wscid % 25 == 0)
                {
                    await SaveStateAsync(db, state, ct);
                    LogSweepProgress(logger, wscid, state.HighestFoundWscid, missStreak);
                }
            }

            if (missStreak >= options.MissStreakStop)
            {
                // Rewind past the miss run so the next sweep re-probes those ids for new registrations.
                state.LastAttemptedWscid = state.HighestFoundWscid;
                state.LastFullCrawlCompletedUtc = DateTime.UtcNow;
            }
        }
        finally
        {
            // Keep the progress of an interrupted or failed sweep so the next one resumes from here.
            await SaveStateAsync(db, state, CancellationToken.None);
            LogSweepFinished(logger, state.HighestFoundWscid);
        }
    }

    private async Task SyncScoringAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var sync = scope.ServiceProvider.GetRequiredService<ScoringSync>();

        await sync.SyncAsync(ct);
    }

    /// <summary>
    /// Re-runs the scoring.dance linker against the registry the crawl just refreshed, then the
    /// swapped-roles repair and the role inference the linker feeds. All three are whole-mirror
    /// passes: the linker and inference only ever fill in a missing id or role, and the repair only
    /// overturns a published role where the registry contradicts it nearly unanimously, so running
    /// them more often costs a few seconds and cannot regress a row.
    /// </summary>
    private async Task ReconcileScoringIdentitiesAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var linked = await ScoringStore.LinkEntriesAsync(db, ct);
        await ScoringStore.RepairSwappedRolesAsync(db, ct);
        var labelled = await ScoringStore.InferRolesAsync(db, ct);

        LogScoringReconciled(logger, linked, labelled);
    }


    private async Task RefreshKnownDancersAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ingest = new DancerIngest(db);

        var recentCutoff = options.RefreshSkipRecent > TimeSpan.Zero ? DateTime.UtcNow - options.RefreshSkipRecent : DateTime.MaxValue;

        var wscids = await db.Dancers.AsNoTracking()
            .Where(d => d.LastCrawledUtc == null || d.LastCrawledUtc < recentCutoff)
            .OrderBy(d => d.LastCrawledUtc)
            .Select(d => d.Wscid)
            .ToListAsync(ct);

        LogRefreshStarted(logger, wscids.Count);

        var started = DateTime.UtcNow;
        Progress = new CrawlProgress(0, wscids.Count, started);

        foreach (var (done, wscid) in wscids.Index())
        {
            ct.ThrowIfCancellationRequested();

            var dto = await FindReadableAsync(wscid, ct);
            if (dto is not null)
            {
                await ingest.IngestAsync(dto, ct);
            }
            else
            {
                await db.Dancers
                    .Where(d => d.Wscid == wscid)
                    .ExecuteUpdateAsync(s => s.SetProperty(d => d.LastCrawledUtc, DateTime.UtcNow), ct);
            }

            Progress = new CrawlProgress(done + 1, wscids.Count, started);
        }
    }

    /// <summary>
    /// Looks a dancer up, treating a record the registry sent in a shape the reader cannot make sense of as
    /// absent: one bad dancer is logged and skipped rather than ending a sweep or a refresh of every other.
    /// </summary>
    private async Task<DancerResponseDto?> FindReadableAsync(int wscid, CancellationToken ct)
    {
        try
        {
            return await client.FindByWscidAsync(wscid, ct);
        }
        catch (JsonException ex)
        {
            LogDancerUnreadable(logger, ex, wscid);
            return null;
        }
    }

    private static async Task<CrawlState> GetStateAsync(AppDbContext db, CancellationToken ct) =>
        await db.CrawlStates.AsNoTracking().FirstOrDefaultAsync(c => c.Id == 1, ct) ?? new CrawlState { Id = 1 };

    /// <summary>Persists the state row without tracking it, since ingesting clears the change tracker.</summary>
    private static async Task SaveStateAsync(AppDbContext db, CrawlState state, CancellationToken ct)
    {
        var updated = await db.CrawlStates
            .Where(c => c.Id == 1)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.LastAttemptedWscid, state.LastAttemptedWscid)
                .SetProperty(c => c.HighestFoundWscid, state.HighestFoundWscid)
                .SetProperty(c => c.LastFullCrawlCompletedUtc, state.LastFullCrawlCompletedUtc), ct);

        if (updated is 0)
        {
            db.CrawlStates.Add(state);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Sweep starting at WSCID {Wscid}")]
    private static partial void LogSweepStarted(ILogger logger, int wscid);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sweep at {Wscid}, highest found {Highest}, miss streak {MissStreak}")]
    private static partial void LogSweepProgress(ILogger logger, int wscid, int highest, int missStreak);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sweep finished; highest WSCID found {Highest}")]
    private static partial void LogSweepFinished(ILogger logger, int highest);

    [LoggerMessage(Level = LogLevel.Information, Message = "Refreshing {Count} mirrored dancers")]
    private static partial void LogRefreshStarted(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Scoring identities reconciled: {Linked} rows linked, {Labelled} roles inferred")]
    private static partial void LogScoringReconciled(ILogger logger, int linked, int labelled);


    [LoggerMessage(Level = LogLevel.Warning, Message = "The registry record for WSCID {Wscid} could not be read; skipping it")]
    private static partial void LogDancerUnreadable(ILogger logger, Exception ex, int wscid);


    [LoggerMessage(Level = LogLevel.Information, Message = "{Command} stopped at its time limit; the next run resumes from here")]
    private static partial void LogStoppedEarly(ILogger logger, CrawlCommand command);
}
