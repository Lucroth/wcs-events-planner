using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WcsEvents.Sync.Calendar;
using WcsEvents.Sync.Data;

namespace WcsEvents.Sync.Publish;

/// <summary>One event's dates and place with the admin's corrections applied.</summary>
public sealed record Trip(string Id, DateOnly Start, DateOnly End, string? City, string? Country, AdminInfo Info, string? Address = null)
{
    private static Trip Of(string id, DateOnly from, DateOnly to, string? city, string? country, IReadOnlyDictionary<string, AdminInfo> infos, string? address = null)
    {
        var info = infos.GetValueOrDefault(id) ?? AdminInfo.Empty;
        return new Trip(id, info.DateFrom ?? from, info.DateTo ?? info.DateFrom ?? to, info.City ?? city, info.Country ?? country, info, address);
    }

    /// <summary>
    /// Every event worth travelling to: the scraped ones (with the WSDC calendar's dates where it
    /// lists them), the calendar editions scoring.dance lacks, and the hand-made ones. The last two
    /// live only in Firestore.
    /// </summary>
    public static async Task<IReadOnlyList<Trip>> LoadAsync(AppDbContext db, FirestoreStore store, CalendarPlanner planner, CancellationToken ct)
    {
        var infos = await EventPublisher.AdminInfosAsync(store, ct);
        var manual = await store.ReadWhereAsync("events", "manual", true, ct);
        var announced = (await store.ReadAllAsync("events", ct)).Where(e => e.Key.StartsWith('x') || e.Key.StartsWith("w-", StringComparison.Ordinal));
        var scraped = await db.ScoringEvents.AsNoTracking()
            .Where(e => e.DateFrom != null && e.Name != "")
            .ToListAsync(ct);

        var plan = await planner.PlanAsync(scraped, ct);
        if (plan is not null)
        {
            scraped.ForEach(e => plan.Apply(e));
        }

        return
        [
            .. scraped.Select(e => Of(e.Id.ToString(), e.DateFrom!.Value, e.DateTo ?? e.DateFrom!.Value, e.City, e.Country, infos, plan?.Matched.GetValueOrDefault(e.Id)?.Address)),
            .. manual.Concat(announced).Select(m => Of(m.Key, Date(m.Value, "dateFrom"), Date(m.Value, "dateTo"), Str(m.Value, "city"), Str(m.Value, "country"), infos, Str(m.Value, "venueAddress"))),
        ];
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String ? v.GetString() : null;

    private static DateOnly Date(JsonElement e, string name) =>
        DateOnly.TryParseExact(Str(e, name), "yyyy-MM-dd", out var d) ? d : DateOnly.MinValue;
}
