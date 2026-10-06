using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;

namespace WcsEvents.Sync.Travel;

/// <summary>
/// Wizz Air's timetable search, the API behind its fare calendar. The API lives under a build number
/// that changes with every release of their site, so it is read off www.wizzair.com/buildnumber first.
/// Unofficial and guarded by bot protection that tolerates only slow, spaced-out requests: any
/// failure means "no Wizz fares", never an error page.
/// </summary>
public sealed partial class WizzClient(HttpClient http, IMemoryCache cache, ILogger<WizzClient> logger)
{
    public async Task<(IReadOnlyList<FlightLeg> Out, IReadOnlyList<FlightLeg> Back)> TimetableAsync(
        string from, string to, (DateOnly From, DateOnly To) outbound, (DateOnly From, DateOnly To) back, CancellationToken ct)
    {
        var api = await ApiBaseAsync(ct);
        var body = new
        {
            flightList = new[]
            {
                new { departureStation = from, arrivalStation = to, from = Iso(outbound.From), to = Iso(outbound.To) },
                new { departureStation = to, arrivalStation = from, from = Iso(back.From), to = Iso(back.To) },
            },
            priceType = "regular",
            adultCount = 1,
            childCount = 0,
            infantCount = 0,
        };

        // Sent with a Content-Length: Wizz refuses a chunked body with "InvalidProtocol".
        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await http.PostAsync($"{api}/Api/search/timetable", content, ct);
        if (!response.IsSuccessStatusCode)
        {
            // Wizz answers a route it does not fly with 400 "InvalidMarket". Its bot protection answers
            // requests that come too close together with 400 "InvalidProtocol", then 503 for a while.
            var reason = await response.Content.ReadAsStringAsync(ct);
            if (!reason.Contains("InvalidMarket"))
            {
                LogRejected(logger, from, to, (int)response.StatusCode, reason.Length > 200 ? reason[..200] : reason);
            }

            return ([], []);
        }

        return Parse(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Every airport Wizz flies to, with the airports it connects to directly.</summary>
    public async Task<IReadOnlyList<(Airport Airport, IReadOnlySet<string> Connections)>> MapAsync(CancellationToken ct)
    {
        var api = await ApiBaseAsync(ct);
        using var response = await http.GetAsync($"{api}/Api/asset/map?languageCode=en-gb", ct);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        // "Milan (All Airports)" and the like are fake stations that resell the real airports' flights.
        return [.. json.RootElement.GetProperty("cities").EnumerateArray()
            .Where(c => !(c.TryGetProperty("isFakeStation", out var fake) && fake.ValueKind is JsonValueKind.True))
            .Select(c => (
            new Airport(
                c.GetProperty("iata").GetString()!,
                c.GetProperty("shortName").GetString()!,
                c.GetProperty("countryCode").GetString()!.ToUpperInvariant(),
                c.GetProperty("latitude").GetDouble(),
                c.GetProperty("longitude").GetDouble()),
            (IReadOnlySet<string>)c.GetProperty("connections").EnumerateArray()
                .Select(x => x.GetProperty("iata").GetString()!).ToHashSet()))];
    }

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private async Task<string> ApiBaseAsync(CancellationToken ct) =>
        await cache.GetOrCreateAsync("wizz:api", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);
            // The page answers 404 yet still carries the build number in its body.
            using var response = await http.GetAsync("https://www.wizzair.com/buildnumber", ct);
            var page = await response.Content.ReadAsStringAsync(ct);
            return BuildNumber().Match(page) is { Success: true } m
                ? $"https://{m.Value}"
                : throw new InvalidOperationException("Wizz build number not found.");
        }) ?? throw new InvalidOperationException("Wizz build number not found.");

    internal static (IReadOnlyList<FlightLeg> Out, IReadOnlyList<FlightLeg> Back) Parse(string body)
    {
        using var json = JsonDocument.Parse(body);
        return (Read(json.RootElement, "outboundFlights"), Read(json.RootElement, "returnFlights"));
    }

    private static List<FlightLeg> Read(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var flights) || flights.ValueKind is not JsonValueKind.Array)
        {
            return [];
        }

        List<FlightLeg> legs = [];
        foreach (var f in flights.EnumerateArray())
        {
            // "checkPrice" and sold-out days carry no fare worth comparing.
            if (f.GetProperty("priceType").GetString() is not "price"
                || !f.TryGetProperty("price", out var price)
                || price.ValueKind is not JsonValueKind.Object
                || price.GetProperty("amount").GetDecimal() <= 0)
            {
                continue;
            }

            var date = DateOnly.FromDateTime(DateTime.Parse(f.GetProperty("departureDate").GetString()!, CultureInfo.InvariantCulture));
            List<TimeOnly> times = f.TryGetProperty("departureDates", out var dates)
                ? [.. dates.EnumerateArray().Select(d => TimeOnly.FromDateTime(DateTime.Parse(d.GetString()!, CultureInfo.InvariantCulture)))]
                : [];

            legs.Add(new FlightLeg(
                Airline.Wizz,
                f.GetProperty("departureStation").GetString()!,
                f.GetProperty("arrivalStation").GetString()!,
                date, times, null, null,
                price.GetProperty("amount").GetDecimal(),
                price.GetProperty("currencyCode").GetString()!));
        }

        return legs;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Wizz timetable {From}-{To} refused with {Status}: {Reason}")]
    private static partial void LogRejected(ILogger logger, string from, string to, int status, string reason);

    [GeneratedRegex(@"be\.wizzair\.com/[0-9.]+")]
    private static partial Regex BuildNumber();
}
