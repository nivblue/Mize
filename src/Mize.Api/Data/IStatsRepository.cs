using Mize.Api.Stats;

namespace Mize.Api.Data;

/// <summary>What the database holds for one supplier.</summary>
/// <param name="Rows">Number of reservations stored for the supplier (the source of truth for "ingested").</param>
/// <param name="Counters">The saved counters, or <c>null</c> when nothing has been saved for the supplier yet.</param>
public sealed record StoredSupplierStats(long Rows, StatsCounters? Counters);

/// <summary>Persistence of the statistics: reading a supplier's data once, and adding flushed deltas.</summary>
public interface IStatsRepository
{
    /// <summary>Reads, in one statement (one consistent snapshot), the row count and the saved counters of a supplier.</summary>
    /// <param name="supplierId">Supplier id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored values.</returns>
    Task<StoredSupplierStats> LoadAsync(string supplierId, CancellationToken cancellationToken);

    /// <summary>
    /// Adds the given deltas to the saved counters, all suppliers in one transaction (all or nothing). A supplier
    /// without a saved row gets one.
    /// </summary>
    /// <param name="deltas">Per-supplier increments to add.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task AddAsync(IReadOnlyDictionary<string, StatsCounters> deltas, CancellationToken cancellationToken);
}
