using Mize.Api.Models;

namespace Mize.Api.Stats;

/// <summary>Counts, per supplier, how requests were handled.</summary>
public interface ISupplierStatsTracker
{
    /// <summary>Records the outcome of an accepted (non-throttled) request.</summary>
    /// <param name="supplierId">The supplier.</param>
    /// <param name="status">The ingest outcome.</param>
    void RecordOutcome(string supplierId, IngestStatus status);

    /// <summary>Records a request rejected by the rate limiter.</summary>
    /// <param name="supplierId">The supplier.</param>
    void RecordThrottled(string supplierId);

    /// <summary>Returns the current counters; all zeros for a supplier never seen.</summary>
    /// <param name="supplierId">The supplier.</param>
    /// <returns>A consistent snapshot of the supplier's counters.</returns>
    SupplierStats Get(string supplierId);
}
