using WcsEvents.Sync.Data;
using WcsEvents.Sync.Scoring;

namespace WcsEvents.Sync.Calendar;

/// <summary>Fetches the WSDC calendar and matches its European editions against scoring.dance's events.</summary>
public sealed class CalendarPlanner(WsdcCalendar calendar, TimeProvider time)
{
    /// <summary>The plan, or null when the calendar could not be read: callers then leave things as they are.</summary>
    public async Task<CalendarPlan?> PlanAsync(IReadOnlyList<ScoringEvent> events, CancellationToken ct) =>
        await calendar.LoadAsync(ct) is { } entries
            ? CalendarPlan.Build(
                CalendarPlan.European(entries, events),
                events,
                DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime))
            : null;
}
