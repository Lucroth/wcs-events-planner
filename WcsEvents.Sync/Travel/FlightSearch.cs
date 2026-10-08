using Microsoft.Extensions.Caching.Memory;

namespace WcsEvents.Sync.Travel;

public sealed record FlightResults(
    IReadOnlyList<FlightLeg> Out,
    IReadOnlyList<FlightLeg> Back,
    IReadOnlyList<FlightCombo> Combos);

/// <summary>
/// Real fares from Ryanair and Wizz between the reader's home airports and the airports near the
/// event, for the day before or of the start and the day of or after the end. Results are cached
/// for six hours per route set: low-cost fares move slowly, and both APIs are unofficial enough that
/// hammering them is the quickest way to lose them.
/// <para>
/// Wizz refuses requests from data-centre networks, so the cloud job passes the Wizz fares a run on
/// a home connection last stored (<paramref name="knownWizz"/>) instead of asking Wizz itself.
/// </para>
/// </summary>
public sealed partial class FlightSearch(
    RyanairClient ryanair, WizzClient wizz, Places places, IMemoryCache cache, ILogger<FlightSearch> logger)
{
    public const string Currency = "PLN";

    public async Task<FlightResults> SearchAsync(
        IReadOnlyList<string> home, IReadOnlyList<string> destinations, DateOnly start, DateOnly end, CancellationToken ct,
        IReadOnlyList<FlightLeg>? knownWizz = null)
    {
        var key = $"flights:{string.Join(',', home)}:{string.Join(',', destinations)}:{start}:{end}:{knownWizz is null}";

        return await cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(6);
            return await FetchAsync(home, destinations, start, end, knownWizz, ct);
        }) ?? new FlightResults([], [], []);
    }

    private async Task<FlightResults> FetchAsync(
        IReadOnlyList<string> home, IReadOnlyList<string> destinations, DateOnly start, DateOnly end,
        IReadOnlyList<FlightLeg>? knownWizz, CancellationToken ct)
    {
        var outWindow = FlightCombos.OutboundWindow(start);
        var backWindow = FlightCombos.ReturnWindow(end);
        HashSet<string> homeSet = [.. home];
        HashSet<string> destSet = [.. destinations];

        List<Task<IReadOnlyList<FlightLeg>>> outTasks = [];
        List<Task<IReadOnlyList<FlightLeg>>> backTasks = [];

        foreach (var h in home)
        {
            outTasks.Add(Safe("ryanair", () => RyanairAsync(h, null, outWindow, ct)));
        }

        foreach (var d in destinations)
        {
            backTasks.Add(Safe("ryanair", () => RyanairAsync(d, "pl", backWindow, ct)));
        }

        List<Task<(IReadOnlyList<FlightLeg> Out, IReadOnlyList<FlightLeg> Back)>> wizzTasks = [];
        if (knownWizz is not null)
        {
            bool In((DateOnly From, DateOnly To) w, FlightLeg l) => l.Date >= w.From && l.Date <= w.To;
            wizzTasks.Add(Task.FromResult<(IReadOnlyList<FlightLeg>, IReadOnlyList<FlightLeg>)>((
                [.. knownWizz.Where(l => In(outWindow, l))],
                [.. knownWizz.Where(l => In(backWindow, l))])));
        }
        else
        {
            var wizzRoutes = (await places.WizzMapAsync(ct)).ToDictionary(w => w.Airport.Iata, w => w.Connections);
            foreach (var h in home)
            {
                foreach (var d in destinations)
                {
                    if (wizzRoutes.TryGetValue(h, out var connections) && connections.Contains(d))
                    {
                        wizzTasks.Add(SafePair(() => wizz.TimetableAsync(h, d, outWindow, backWindow, ct)));
                    }
                }
            }
        }

        var outs = (await Task.WhenAll(outTasks)).SelectMany(x => x).Where(l => destSet.Contains(l.To));
        var backs = (await Task.WhenAll(backTasks)).SelectMany(x => x).Where(l => homeSet.Contains(l.To));
        var wizzLegs = await Task.WhenAll(wizzTasks);

        // Wizz may answer with flights from a neighbouring airport it considers equivalent; only the
        // airports asked about count.
        var wizzOut = wizzLegs.SelectMany(w => w.Out).Where(l => homeSet.Contains(l.From) && destSet.Contains(l.To));
        var wizzBack = wizzLegs.SelectMany(w => w.Back).Where(l => destSet.Contains(l.From) && homeSet.Contains(l.To));

        List<FlightLeg> allOut = [.. outs.Concat(wizzOut).DistinctBy(Key).OrderBy(l => l.Price)];
        List<FlightLeg> allBack = [.. backs.Concat(wizzBack).DistinctBy(Key).OrderBy(l => l.Price)];

        return new FlightResults(allOut, allBack, FlightCombos.Cheapest(allOut, allBack, Currency, 10));
    }

    /// <summary>
    /// One Ryanair query answers every event sharing an airport and a window: all routes out of an
    /// airport come back at once. Remembered for the run so the daily job asks each only once.
    /// </summary>
    private async Task<IReadOnlyList<FlightLeg>> RyanairAsync(string from, string? toCountry, (DateOnly From, DateOnly To) window, CancellationToken ct) =>
        await cache.GetOrCreateAsync($"ryanair:{from}:{toCountry}:{window.From}:{window.To}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(6);
            return await ryanair.OneWayAsync(from, toCountry, window.From, window.To, ct);
        }) ?? [];

    private static (Airline, string, string, DateOnly, decimal) Key(FlightLeg l) => (l.Airline, l.From, l.To, l.Date, l.Price);

    private async Task<IReadOnlyList<FlightLeg>> Safe(string source, Func<Task<IReadOnlyList<FlightLeg>>> call)
    {
        try
        {
            return await call();
        }
        catch (Exception ex)
        {
            LogSourceFailed(logger, source, ex);
            return [];
        }
    }

    private async Task<(IReadOnlyList<FlightLeg> Out, IReadOnlyList<FlightLeg> Back)> SafePair(
        Func<Task<(IReadOnlyList<FlightLeg>, IReadOnlyList<FlightLeg>)>> call)
    {
        try
        {
            return await call();
        }
        catch (Exception ex)
        {
            LogSourceFailed(logger, "wizz", ex);
            return ([], []);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fare lookup on {Source} failed; showing what the others returned")]
    private static partial void LogSourceFailed(ILogger logger, string source, Exception ex);
}
