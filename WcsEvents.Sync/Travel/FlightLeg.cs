namespace WcsEvents.Sync.Travel;

public enum Airline
{
    Ryanair,
    Wizz,
}

/// <summary>
/// One direct one-way flight at its cheapest fare. Wizz publishes the departure times of the day but
/// not which one the fare belongs to, nor arrival times, so for Wizz <see cref="Arrival"/> is null and
/// <see cref="Times"/> lists every departure that day.
/// </summary>
public sealed record FlightLeg(
    Airline Airline,
    string From,
    string To,
    DateOnly Date,
    IReadOnlyList<TimeOnly> Times,
    DateTime? Departure,
    DateTime? Arrival,
    decimal Price,
    string Currency)
{
    public TimeSpan? Duration => Departure is { } d && Arrival is { } a ? a - d : null;
}

public sealed record FlightCombo(FlightLeg Out, FlightLeg Back)
{
    public decimal PerPerson => Out.Price + Back.Price;
}
