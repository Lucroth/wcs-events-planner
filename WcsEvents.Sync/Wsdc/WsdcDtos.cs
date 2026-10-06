using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WcsEvents.Sync.Wsdc;

public sealed record LookupNameDto(
    [property: JsonPropertyName("first_name")] string FirstName,
    [property: JsonPropertyName("last_name")] string LastName,
    [property: JsonPropertyName("wscid")] int Wscid);

public sealed record NamesResponseDto(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("names")] IReadOnlyList<LookupNameDto> Names);

public sealed record DancerDto(
    [property: JsonPropertyName("first_name")] string FirstName,
    [property: JsonPropertyName("last_name")] string LastName,
    [property: JsonPropertyName("wscid")] int Wscid);

public sealed record DivisionDto(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("abbreviation")] string Abbreviation);

public sealed record EventDto(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("location")] string? Location,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("date")] string Date);

public sealed record CompetitionDto(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("points")][property: JsonConverter(typeof(LenientInt32Converter))] int Points,
    [property: JsonPropertyName("event")] EventDto Event,
    [property: JsonPropertyName("result")] string Result);

public sealed record DivisionPlacementsDto(
    [property: JsonPropertyName("division")] DivisionDto Division,
    [property: JsonPropertyName("total_points")][property: JsonConverter(typeof(LenientInt32Converter))] int TotalPoints,
    [property: JsonPropertyName("competitions")] IReadOnlyList<CompetitionDto> Competitions);

public sealed record LevelDto(
    [property: JsonPropertyName("required")] string? Required,
    [property: JsonPropertyName("allowed")] string? Allowed);

/// <summary>
/// Registry response for a WSCID lookup. <see cref="Placements"/> is keyed by dance style
/// ("West Coast Swing") then by division abbreviation.
/// </summary>
public sealed record DancerResponseDto(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("dancer")] DancerDto Dancer,
    [property: JsonPropertyName("level")] LevelDto? Level,
    [property: JsonPropertyName("placements")][property: JsonConverter(typeof(PlacementsConverter))]
    Dictionary<string, Dictionary<string, DivisionPlacementsDto>>? Placements);

/// <summary>Reads a points figure the registry has sent as a whole number, a decimal, a numeric string or null.
/// One odd value must not make a dancer unreadable, so a decimal is rounded and null or text that is not a
/// number is zero; anything that is not a scalar is still an error.</summary>
public sealed class LenientInt32Converter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.Number => reader.TryGetInt32(out var whole) ? whole : Round(reader.GetDouble()),
        JsonTokenType.String when double.TryParse(reader.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => Round(parsed),
        JsonTokenType.String or JsonTokenType.Null => 0,
        _ => throw new JsonException($"Expected a number for points but found {reader.TokenType}."),
    };

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value);

    private static int Round(double value) =>
        double.IsFinite(value) ? (int)Math.Clamp(Math.Round(value), int.MinValue, int.MaxValue) : 0;
}

/// <summary>The registry sends an empty JSON array instead of an object when a dancer has no placements.</summary>
public sealed class PlacementsConverter : JsonConverter<Dictionary<string, Dictionary<string, DivisionPlacementsDto>>?>
{
    public override Dictionary<string, Dictionary<string, DivisionPlacementsDto>>? Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType is JsonTokenType.StartArray)
        {
            reader.Skip();
            return null;
        }

        return JsonSerializer.Deserialize(ref reader, WsdcJsonContext.Default.DictionaryStringDictionaryStringDivisionPlacementsDto);
    }

    public override void Write(
        Utf8JsonWriter writer, Dictionary<string, Dictionary<string, DivisionPlacementsDto>>? value, JsonSerializerOptions options) =>
        throw new NotSupportedException();
}

[JsonSerializable(typeof(NamesResponseDto))]
[JsonSerializable(typeof(DancerResponseDto))]
[JsonSerializable(typeof(Dictionary<string, Dictionary<string, DivisionPlacementsDto>>))]
internal sealed partial class WsdcJsonContext : JsonSerializerContext;
