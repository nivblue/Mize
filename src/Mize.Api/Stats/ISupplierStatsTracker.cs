using Mize.Api.Models;

namespace Mize.Api.Stats;

/// <summary>
/// Per-supplier statistics. Reads are served from memory (after one database read per supplier); recording never
/// touches the database. See <see cref="SupplierStatsCache"/> for the rules that keep the cache correct.
/// </summary>
public interface ISupplierStatsTracker
{
    /// <summary>
    /// Records the outcome of an accepted request. Must be called by the ingest logic while it holds the write gate,
    /// immediately after the database write has committed (this is what keeps the cached row count exact).
    /// </summary>
    /// <param name="supplierId">The supplier.</param>
    /// <param name="status">The ingest outcome.</param>
    void RecordOutcome(string supplierId, IngestStatus status);

    /// <summary>Records a request rejected by the rate limiter.</summary>
    /// <param name="supplierId">The supplier.</param>
    void RecordThrottled(string supplierId);

    /// <summary>
    /// Forgets the cached reservation count of a supplier because a write failed in a way that leaves it unknown whether
    /// it committed. The next read reloads the count from the database; the request counters are kept.
    /// </summary>
    /// <param name="supplierId">The supplier.</param>
    void InvalidateRows(string supplierId);

    /// <summary>
    /// Returns the supplier's statistics: <c>ingested</c> is the number of reservations stored (duplicates and updates do
    /// not add to it), the rest are request counters. Zeros for a supplier without data.
    /// </summary>
    /// <param name="supplierId">The supplier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A consistent snapshot.</returns>
    Task<SupplierStats> GetAsync(string supplierId, CancellationToken cancellationToken);
}
