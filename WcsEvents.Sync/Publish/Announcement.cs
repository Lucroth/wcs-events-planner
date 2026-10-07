using System.Text.Json;

namespace WcsEvents.Sync.Publish;

/// <summary>
/// The dates an organiser has announced for a series' next edition before it is listed on
/// scoring.dance, keyed in <c>data/announced.json</c> by the id of the latest listed edition.
/// Shown on that series' expected row until the real listing replaces it.
/// </summary>
public sealed record Announcement(DateOnly DateFrom, DateOnly DateTo, string? City, string? Venue, string? WebsiteUrl, string? Source)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IReadOnlyDictionary<string, Announcement> Load(string file) =>
        File.Exists(file)
            ? JsonSerializer.Deserialize<Dictionary<string, Announcement>>(File.ReadAllText(file), Json) ?? []
            : new Dictionary<string, Announcement>();
}
