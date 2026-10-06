using System.Globalization;
using System.Text.Json;

namespace WcsEvents.Sync.Travel;

/// <summary>
/// Ryanair's fare finder, the API behind its own "cheap flights" pages. Unofficial and unversioned:
/// expect it to change, and treat any failure as "no Ryanair fares" rather than an error.
/// It returns the cheapest fare per route over the whole date range asked for.
/// </summary>
public sealed class RyanairClient(HttpClient http)
{
    public async Task<IReadOnlyList<FlightLeg>> OneWayAsync(
        string from, string? toCountry, DateOnly dateFrom, DateOnly dateTo, CancellationToken ct)
    {
        var url = $"api/farfnd/v4/oneWayFares?departureAirportIataCode={from}" +
            (toCountry is null ? "" : $"&arrivalCountryCode={toCountry}") +
            $"&outboundDepartureDateFrom={dateFrom:yyyy-MM-dd}&outboundDepartureDateTo={dateTo:yyyy-MM-dd}" +
            "&currency=PLN&market=pl-pl&adultPaxCount=1";

        using var response = await http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        return Parse(await response.Content.ReadAsStringAsync(ct));
    }

    public async Task<IReadOnlyList<Airport>> AirportsAsync(CancellationToken ct)
    {
        using var response = await http.GetAsync("api/views/locate/5/airports/en/active", ct);
        response.EnsureSuccessStatusCode();

        return ParseAirports(await response.Content.ReadAsStringAsync(ct));
    }

    internal static IReadOnlyList<Airport> ParseAirports(string body)
    {
        using var json = JsonDocument.Parse(body);

        return [.. json.RootElement.EnumerateArray().Select(a => new Airport(
            a.GetProperty("code").GetString()!,
            a.GetProperty("name").GetString()!,
            a.GetProperty("country").GetProperty("code").GetString()!.ToUpperInvariant(),
            a.GetProperty("coordinates").GetProperty("latitude").GetDouble(),
            a.GetProperty("coordinates").GetProperty("longitude").GetDouble()))];
    }

    internal static IReadOnlyList<FlightLeg> Parse(string body)
    {
        using var json = JsonDocument.Parse(body);
        if (!json.RootElement.TryGetProperty("fares", out var fares) || fares.ValueKind is not JsonValueKind.Array)
        {
            return [];
        }

        List<FlightLeg> legs = [];
        foreach (var fare in fares.EnumerateArray())
        {
            var o = fare.GetProperty("outbound");
            var departure = DateTime.Parse(o.GetProperty("departureDate").GetString()!, CultureInfo.InvariantCulture);
            var arrival = DateTime.Parse(o.GetProperty("arrivalDate").GetString()!, CultureInfo.InvariantCulture);
            var price = o.GetProperty("price");

            legs.Add(new FlightLeg(
                Airline.Ryanair,
                o.GetProperty("departureAirport").GetProperty("iataCode").GetString()!,
                o.GetProperty("arrivalAirport").GetProperty("iataCode").GetString()!,
                DateOnly.FromDateTime(departure),
                [TimeOnly.FromDateTime(departure)],
                departure,
                arrival,
                price.GetProperty("value").GetDecimal(),
                price.GetProperty("currencyCode").GetString()!));
        }

        return legs;
    }
}
