using System.Net;

namespace WcsEvents.Sync.Scoring;

/// <summary>Fetches scoring.dance result pages. Plain HTML; there is no API.</summary>
public sealed class ScoringClient(HttpClient http)
{
    /// <summary>The home page, which carries the upcoming listing.</summary>
    public Task<string?> GetHomeAsync(CancellationToken ct) =>
        GetAsync("enUS/", ct);

    public Task<string?> GetRecentAsync(CancellationToken ct) =>
        GetAsync("enUS/recent", ct);

    public Task<string?> GetEventResultsAsync(int scoringEventId, CancellationToken ct) =>
        GetAsync($"enUS/events/{scoringEventId}/results/", ct);

    public Task<string?> GetRoundAsync(int scoringEventId, int roundId, CancellationToken ct) =>
        GetAsync($"enUS/events/{scoringEventId}/results/{roundId}.html", ct);

    /// <summary>
    /// The marshalling "wall" for a live event — every round it has scheduled, as a button per round.
    /// Published before results exist, so this is the only page that can say who's actually dancing
    /// right now.
    /// </summary>
    public Task<string?> GetWallAsync(int scoringEventId, CancellationToken ct) =>
        GetAsync($"enUS/events/{scoringEventId}/wall", ct);

    /// <summary>One round's heats off the wall — bib, name, and running order, no result of any kind.</summary>
    public Task<string?> GetWallRoundAsync(int scoringEventId, int roundId, CancellationToken ct) =>
        GetAsync($"enUS/events/{scoringEventId}/wall?act=ajaxround&round={roundId}", ct);

    /// <summary>Null when the page does not exist.</summary>
    private async Task<string?> GetAsync(string path, CancellationToken ct)
    {
        using var response = await http.GetAsync(path, ct);
        if (response.StatusCode is HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }
}
