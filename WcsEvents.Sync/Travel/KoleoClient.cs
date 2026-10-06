using System.Globalization;
using System.Text.Json;

namespace WcsEvents.Sync.Travel;

public sealed record TrainLeg(DateTime Departure, DateTime Arrival, int Changes, decimal Price);

/// <summary>
/// koleo.pl's own API, the one its timetable pages run on: connections between two stations, and
/// the fare of each. Unofficial; any failure means "no train prices", never an error page.
/// PKP Intercity sells only about a month ahead, so fares further out come back without a valid price.
/// </summary>
public sealed class KoleoClient(HttpClient http)
{
    /// <summary>How many departures of a day get priced: one request each.</summary>
    private const int PricedPerDay = 4;

    /// <summary>Earliest departure worth offering.</summary>
    private const int FirstHour = 6;

    public async Task<IReadOnlyList<TrainLeg>> DayAsync(string fromSlug, string toSlug, DateOnly date, CancellationToken ct)
    {
        var when = Uri.EscapeDataString($"{date:dd-MM-yyyy} {FirstHour:00}:00:00");
        var url = $"api/v2/main/connections?query%5Bdate%5D={when}&query%5Bstart_station%5D={fromSlug}&query%5Bend_station%5D={toSlug}" +
            "&query%5Bonly_purchasable%5D=true&query%5Bonly_direct%5D=false";

        using var response = await http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        List<TrainLeg> legs = [];
        foreach (var (id, departure, arrival, changes) in ParseConnections(await response.Content.ReadAsStringAsync(ct))
            .Where(c => DateOnly.FromDateTime(c.Departure) == date)
            .Take(PricedPerDay))
        {
            if (await PriceAsync(id, ct) is { } price)
            {
                legs.Add(new TrainLeg(departure, arrival, changes, price));
            }
        }

        return legs;
    }

    private async Task<decimal?> PriceAsync(long connectionId, CancellationToken ct)
    {
        using var response = await http.GetAsync($"api/v2/main/connections/{connectionId}/price", ct);
        return response.IsSuccessStatusCode ? ParsePrice(await response.Content.ReadAsStringAsync(ct)) : null;
    }

    internal static IReadOnlyList<(long Id, DateTime Departure, DateTime Arrival, int Changes)> ParseConnections(string body)
    {
        using var json = JsonDocument.Parse(body);
        if (!json.RootElement.TryGetProperty("connections", out var list) || list.ValueKind is not JsonValueKind.Array)
        {
            return [];
        }

        return [.. list.EnumerateArray()
            .Where(c => c.TryGetProperty("purchasable", out var p) && p.ValueKind is JsonValueKind.True)
            .Select(c => (
                c.GetProperty("id").GetInt64(),
                Local(c.GetProperty("departure").GetString()!),
                Local(c.GetProperty("arrival").GetString()!),
                c.TryGetProperty("changes", out var ch) && ch.ValueKind is JsonValueKind.Number ? ch.GetInt32() : 0))
            .OrderBy(c => c.Item2)];
    }

    /// <summary>The cheapest valid one-way fare for one adult; null when none is on sale yet.</summary>
    internal static decimal? ParsePrice(string body)
    {
        using var json = JsonDocument.Parse(body);
        if (!json.RootElement.TryGetProperty("prices", out var prices) || prices.ValueKind is not JsonValueKind.Array)
        {
            return null;
        }

        var valid = prices.EnumerateArray()
            .Where(p => p.TryGetProperty("valid_price", out var v) && v.ValueKind is JsonValueKind.True)
            .Select(p => decimal.TryParse(p.GetProperty("value").GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : 0)
            .Where(d => d > 0)
            .ToList();

        return valid.Count is 0 ? null : valid.Min();
    }

    /// <summary>koleo writes Polish local time with its offset; the offset is dropped so the time reads as on the ticket.</summary>
    private static DateTime Local(string iso) => DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture).DateTime;
}
