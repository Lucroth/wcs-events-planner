namespace WcsEvents.Sync.Travel;

/// <summary>Where a reader sets off from: the station koleo knows it by, and the airports worth flying from.</summary>
public sealed record HomeCity(string Name, string KoleoSlug, IReadOnlyList<string> Airports)
{
    /// <summary>A city with no airport of its own flies from Warsaw.</summary>
    public IReadOnlyList<string> FlightOrigins => Airports.Count > 0 ? Airports : ["WAW", "WMI"];
}

public static class HomeCities
{
    public static readonly IReadOnlyList<HomeCity> All =
    [
        new("Warszawa", "warszawa", ["WAW", "WMI"]),
        new("Kraków", "krakow", ["KRK"]),
        new("Wrocław", "wroclaw-glowny", ["WRO"]),
        new("Poznań", "poznan-glowny", ["POZ"]),
        new("Gdańsk", "gdansk", ["GDN"]),
        new("Katowice", "katowice", ["KTW"]),
        new("Łódź", "lodz", ["LCJ"]),
        new("Szczecin", "szczecin-glowny", ["SZZ"]),
        new("Lublin", "lublin-glowny", ["LUZ"]),
        new("Rzeszów", "rzeszow-glowny", ["RZE"]),
        new("Bydgoszcz", "bydgoszcz-glowna", ["BZG"]),
        new("Białystok", "bialystok", []),
        new("Toruń", "torun", ["BZG"]),
        new("Olsztyn", "olsztyn", ["SZY"]),
    ];

    /// <summary>Names the flight results for one set of origin airports; the frontend builds the same key.</summary>
    public static string Key(IReadOnlyList<string> airports) => string.Join("-", airports);

    public static HomeCity Find(string? name) =>
        All.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) ?? All[0];
}
