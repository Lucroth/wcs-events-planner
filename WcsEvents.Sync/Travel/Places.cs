using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace WcsEvents.Sync.Travel;

public sealed record Airport(string Iata, string Name, string CountryCode, double Latitude, double Longitude);

public sealed record Station(string Slug, string Name, double Latitude, double Longitude, int Hits);

public static class Geo
{
    public static double Km(double lat1, double lng1, double lat2, double lng2)
    {
        static double Rad(double d) => d * Math.PI / 180;

        var dLat = Rad(lat2 - lat1);
        var dLng = Rad(lng2 - lng1);
        var a = (Math.Sin(dLat / 2) * Math.Sin(dLat / 2))
            + (Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLng / 2) * Math.Sin(dLng / 2));

        return 6371 * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    /// <summary>The airports within reach of a point, nearest first.</summary>
    public static IReadOnlyList<Airport> NearestAirports(IEnumerable<Airport> airports, double lat, double lng, double maxKm = 200, int take = 3) =>
        [.. airports
            .Select(a => (Airport: a, Km: Km(lat, lng, a.Latitude, a.Longitude)))
            .Where(x => x.Km <= maxKm)
            .OrderBy(x => x.Km)
            .Take(take)
            .Select(x => x.Airport)];

    /// <summary>
    /// The station a traveller to this point would actually book to: the busiest one nearby, not the
    /// nearest, which is usually a suburban halt.
    /// </summary>
    public static Station? MainStation(IEnumerable<Station> stations, double lat, double lng, double maxKm = 15) =>
        stations
            .Where(s => Km(lat, lng, s.Latitude, s.Longitude) <= maxKm)
            .MaxBy(s => s.Hits);
}

/// <summary>
/// Reference data that changes rarely and costs a request to fetch: where airports and stations are,
/// which routes Wizz flies, and where a city is. Each piece is cached for a day; a failure leaves it
/// empty, so travel degrades to links instead of breaking the page.
/// </summary>
public sealed partial class Places(
    RyanairClient ryanair, WizzClient wizz, IHttpClientFactory httpFactory, IMemoryCache cache, ILogger<Places> logger)
{
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    private readonly SemaphoreSlim nominatimTurn = new(1, 1);

    public async Task<IReadOnlyList<Airport>> AirportsAsync(CancellationToken ct)
    {
        var ryan = await CachedAsync("places:ryanair", ryanair.AirportsAsync, ct) ?? [];
        var wz = await WizzMapAsync(ct);

        return [.. ryan.Concat(wz.Select(w => w.Airport)).DistinctBy(a => a.Iata)];
    }

    public async Task<IReadOnlyList<(Airport Airport, IReadOnlySet<string> Connections)>> WizzMapAsync(CancellationToken ct) =>
        await CachedAsync("places:wizz", wizz.MapAsync, ct) ?? [];

    public async Task<IReadOnlyList<Station>> StationsAsync(CancellationToken ct) =>
        await CachedAsync("places:koleo", async c =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://koleo.pl/api/v2/main/stations");
            request.Headers.Add("X-KOLEO-Version", "1");
            using var response = await httpFactory.CreateClient("travel").SendAsync(request, c);
            response.EnsureSuccessStatusCode();

            return ParseStations(await response.Content.ReadAsStringAsync(c));
        }, ct) ?? [];

    internal static IReadOnlyList<Station> ParseStations(string body)
    {
        using var json = JsonDocument.Parse(body);

        return [.. json.RootElement.EnumerateArray()
            .Where(s => s.GetProperty("latitude").ValueKind is JsonValueKind.Number && s.GetProperty("longitude").ValueKind is JsonValueKind.Number)
            .Select(s => new Station(
                s.GetProperty("name_slug").GetString()!,
                s.GetProperty("name").GetString()!,
                s.GetProperty("latitude").GetDouble(),
                s.GetProperty("longitude").GetDouble(),
                s.TryGetProperty("hits", out var h) && h.ValueKind is JsonValueKind.Number ? h.GetInt32() : 0))];
    }

    /// <summary>OpenStreetMap's Nominatim. Its usage policy allows one request a second with a real User-Agent; results are cached for a month.</summary>
    public async Task<(double Lat, double Lng)?> GeocodeAsync(string query, CancellationToken ct)
    {
        var key = $"places:geo:{query.ToLowerInvariant()}";
        if (cache.TryGetValue(key, out (double, double)? hit))
        {
            return hit;
        }

        (double, double)? found = null;
        await nominatimTurn.WaitAsync(ct);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1.1), ct);

            var body = await httpFactory.CreateClient("nominatim")
                .GetStringAsync($"search?format=json&limit=1&q={Uri.EscapeDataString(query)}", ct);
            using var json = JsonDocument.Parse(body);

            if (json.RootElement.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } first)
            {
                found = (double.Parse(first.GetProperty("lat").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
                    double.Parse(first.GetProperty("lon").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogFailed(logger, "geocode", ex);
            return null;
        }
        finally
        {
            nominatimTurn.Release();
        }

        cache.Set(key, found, TimeSpan.FromDays(30));
        return found;
    }

    private async Task<T?> CachedAsync<T>(string key, Func<CancellationToken, Task<T>> load, CancellationToken ct)
        where T : class
    {
        if (cache.Get<T>(key) is { } hit)
        {
            return hit;
        }

        try
        {
            var value = await load(ct);
            cache.Set(key, value, Day);
            return value;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogFailed(logger, key, ex);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Travel reference data {What} could not be fetched")]
    private static partial void LogFailed(ILogger logger, string what, Exception ex);
}
