using System.Text.Json;

namespace WcsEvents.Sync.Publish;

/// <summary>
/// The parts of an admin-edited <c>info/{id}</c> document the sync has to respect: corrected name,
/// dates and place, and the venue and airports that travel is worked out from. The rest of the
/// document (prices, staff, links, schedules) is only ever read by the frontend.
/// </summary>
public sealed record AdminInfo(
    string? Name,
    DateOnly? DateFrom,
    DateOnly? DateTo,
    string? City,
    string? Country,
    string? VenueAddress,
    double? Latitude,
    double? Longitude,
    IReadOnlyList<string> Airports)
{
    public static readonly AdminInfo Empty = new(null, null, null, null, null, null, null, null, []);

    public static AdminInfo From(JsonElement doc)
    {
        var o = doc.TryGetProperty("override", out var ov) && ov.ValueKind is JsonValueKind.Object ? ov : default;

        return new AdminInfo(
            Str(o, "name"),
            Date(o, "dateFrom"),
            Date(o, "dateTo"),
            Str(o, "city"),
            Str(o, "country"),
            Str(doc, "venueAddress"),
            Num(doc, "lat"),
            Num(doc, "lng"),
            doc.TryGetProperty("airports", out var a) && a.ValueKind is JsonValueKind.Array
                ? [.. a.EnumerateArray().Where(x => x.ValueKind is JsonValueKind.String).Select(x => x.GetString()!.Trim().ToUpperInvariant()).Where(x => x.Length == 3)]
                : []);
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind is JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String && v.GetString() is { Length: > 0 } s
            ? s.Trim()
            : null;

    private static DateOnly? Date(JsonElement e, string name) =>
        Str(e, name) is { } s && DateOnly.TryParseExact(s, "yyyy-MM-dd", out var d) ? d : null;

    private static double? Num(JsonElement e, string name) =>
        e.ValueKind is JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.Number ? v.GetDouble() : null;
}
