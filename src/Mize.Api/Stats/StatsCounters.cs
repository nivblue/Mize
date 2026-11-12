namespace Mize.Api.Stats;

/// <summary>
/// The four per-supplier request counters. Used for totals, for in-memory increments and for the deltas that are
/// added to the stored totals when the cache is flushed.
/// </summary>
/// <param name="Created">Requests that inserted a new reservation.</param>
/// <param name="Updated">Requests that changed an existing reservation.</param>
/// <param name="Unchanged">Requests that were exact duplicates (nothing written).</param>
/// <param name="Throttled">Requests rejected by the rate limiter.</param>
public sealed record StatsCounters(long Created, long Updated, long Unchanged, long Throttled)
{
    /// <summary>All counters zero.</summary>
    public static StatsCounters Zero { get; } = new(0, 0, 0, 0);

    /// <summary>True when every counter is zero.</summary>
    public bool IsZero => Created == 0 && Updated == 0 && Unchanged == 0 && Throttled == 0;

    /// <summary>Adds two sets of counters field by field.</summary>
    public static StatsCounters operator +(StatsCounters a, StatsCounters b) =>
        new(a.Created + b.Created, a.Updated + b.Updated, a.Unchanged + b.Unchanged, a.Throttled + b.Throttled);

    /// <summary>Subtracts two sets of counters field by field.</summary>
    public static StatsCounters operator -(StatsCounters a, StatsCounters b) =>
        new(a.Created - b.Created, a.Updated - b.Updated, a.Unchanged - b.Unchanged, a.Throttled - b.Throttled);
}
