using System.Net;
using System.Text.Json;

namespace WcsEvents.Sync.Wsdc;

/// <summary>Typed client over the public WSDC registry lookup endpoint.</summary>
public sealed class WsdcClient(HttpClient http)
{
    private const string LookupPath = "lookup/find";

    /// <summary>
    /// Name autocomplete. The registry answers with a name list, or with a full dancer record
    /// when the query matches exactly one dancer.
    /// </summary>
    public async Task<IReadOnlyList<LookupNameDto>> FindByNameAsync(string query, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent([new("q", query)]);
        using var response = await http.PostAsync(LookupPath, content, ct);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (!document.RootElement.TryGetProperty("type", out var type))
        {
            return [];
        }

        return type.GetString() switch
        {
            "names" => document.RootElement.Deserialize(WsdcJsonContext.Default.NamesResponseDto)?.Names ?? [],
            "dancer" when document.RootElement.Deserialize(WsdcJsonContext.Default.DancerResponseDto) is { Dancer: var d } =>
                [new LookupNameDto(d.FirstName, d.LastName, d.Wscid)],
            _ => [],
        };
    }

    /// <summary>Returns null when the registry has no dancer with that WSCID (404).</summary>
    public async Task<DancerResponseDto?> FindByWscidAsync(int wscid, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent([new("num", wscid.ToString())]);
        using var response = await http.PostAsync(LookupPath, content, ct);
        if (response.StatusCode is HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var parsed = await JsonSerializer.DeserializeAsync(stream, WsdcJsonContext.Default.DancerResponseDto, ct);
        return parsed?.Type is "dancer" ? parsed : null;
    }
}
