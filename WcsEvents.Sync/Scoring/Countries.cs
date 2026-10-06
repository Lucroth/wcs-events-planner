namespace WcsEvents.Sync.Scoring;

/// <summary>
/// Country names, and which of them are European. The upcoming listing gives ISO3 codes while stored
/// rounds carry the spelt-out name, so both have to end up in the same vocabulary.
/// </summary>
public static class Countries
{
    /// <summary>ISO3 to the name scoring.dance uses on a results page for the same country.</summary>
    private static readonly Dictionary<string, string> ByCode = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AUT"] = "Austria",
        ["BEL"] = "Belgium",
        ["BGR"] = "Bulgaria",
        ["CHE"] = "Switzerland",
        ["CZE"] = "Czechia",
        ["DEU"] = "Germany",
        ["DNK"] = "Denmark",
        ["ESP"] = "Spain",
        ["EST"] = "Estonia",
        ["FIN"] = "Finland",
        ["FRA"] = "France",
        ["GBR"] = "United Kingdom",
        ["GRC"] = "Greece",
        ["HRV"] = "Croatia",
        ["HUN"] = "Hungary",
        ["IRL"] = "Ireland",
        ["ISL"] = "Iceland",
        ["ITA"] = "Italy",
        ["LTU"] = "Lithuania",
        ["LVA"] = "Latvia",
        ["NLD"] = "Netherlands",
        ["NOR"] = "Norway",
        ["POL"] = "Poland",
        ["PRT"] = "Portugal",
        ["ROU"] = "Romania",
        ["RUS"] = "Russia",
        ["SVK"] = "Slovakia",
        ["SVN"] = "Slovenia",
        ["SWE"] = "Sweden",
        ["TUR"] = "Turkey",
        ["UKR"] = "Ukraine",
        ["AUS"] = "Australia",
        ["BRA"] = "Brazil",
        ["CAN"] = "Canada",
        ["CHN"] = "China",
        ["ISR"] = "Israel",
        ["JPN"] = "Japan",
        ["KOR"] = "South Korea",
        ["MEX"] = "Mexico",
        ["NZL"] = "New Zealand",
        ["SGP"] = "Singapore",
        ["USA"] = "United States of America",
        ["ZAF"] = "South Africa",
    };

    private static readonly HashSet<string> European = new(StringComparer.OrdinalIgnoreCase)
    {
        "Austria", "Belgium", "Bulgaria", "Switzerland", "Czechia", "Czech Republic", "Germany",
        "Denmark", "Spain", "Estonia", "Finland", "France", "United Kingdom", "Great Britain",
        "Greece", "Croatia", "Hungary", "Ireland", "Iceland", "Italy", "Lithuania", "Latvia",
        "Netherlands", "The Netherlands", "Norway", "Poland", "Portugal", "Romania", "Russia",
        "Slovakia", "Slovenia", "Sweden", "Turkey", "Ukraine", "Serbia", "Luxembourg", "Malta",
        "Cyprus", "Belarus", "Moldova",
    };

    /// <summary>The country name for a code, or the input unchanged when it is already a name.</summary>
    public static string? Name(string? codeOrName) =>
        string.IsNullOrWhiteSpace(codeOrName) ? null
        : ByCode.TryGetValue(codeOrName.Trim(), out var name) ? name
        : codeOrName.Trim();

    public static bool IsEuropean(string? country) =>
        Name(country) is { } name && European.Contains(name);
}
