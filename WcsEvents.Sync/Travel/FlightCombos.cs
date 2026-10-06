namespace WcsEvents.Sync.Travel;

public static class FlightCombos
{
    /// <summary>
    /// The cheapest outbound and return pairs. Legs are independent one-way fares, so any outbound
    /// pairs with any return: different airlines, and a different Polish airport home, are allowed.
    /// Fares in another currency are left out rather than compared at a guessed rate.
    /// </summary>
    public static IReadOnlyList<FlightCombo> Cheapest(
        IEnumerable<FlightLeg> outbound, IEnumerable<FlightLeg> back, string currency, int take)
    {
        var outs = outbound.Where(l => l.Currency == currency).OrderBy(l => l.Price).Take(take).ToList();
        var backs = back.Where(l => l.Currency == currency).OrderBy(l => l.Price).Take(take).ToList();

        return [.. outs
            .SelectMany(o => backs.Select(b => new FlightCombo(o, b)))
            .OrderBy(c => c.PerPerson)
            .ThenBy(c => c.Out.Date)
            .Take(take)];
    }

    /// <summary>Fly out the day before or the day the event starts; come back the day it ends or the day after.</summary>
    public static (DateOnly From, DateOnly To) OutboundWindow(DateOnly start) => (start.AddDays(-1), start);

    public static (DateOnly From, DateOnly To) ReturnWindow(DateOnly end) => (end, end.AddDays(1));
}
