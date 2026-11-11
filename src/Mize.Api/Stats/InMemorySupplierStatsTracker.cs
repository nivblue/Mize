using System.Collections.Concurrent;
using Mize.Api.Models;

namespace Mize.Api.Stats;

/// <summary>Thread-safe in-memory <see cref="ISupplierStatsTracker"/>. Counters reset when the process restarts.</summary>
public sealed class InMemorySupplierStatsTracker : ISupplierStatsTracker
{
    private readonly ConcurrentDictionary<string, Counters> _counters = new(StringComparer.Ordinal);

    /// <summary>Mutable counters for one supplier; <see cref="Gate"/> guards every field.</summary>
    private sealed class Counters
    {
        /// <summary>Lock guarding the counters so snapshots are consistent.</summary>
        public readonly object Gate = new();

        /// <summary>Number of created outcomes.</summary>
        public long Created;

        /// <summary>Number of updated outcomes.</summary>
        public long Updated;

        /// <summary>Number of unchanged outcomes.</summary>
        public long Unchanged;

        /// <summary>Number of stale outcomes.</summary>
        public long Stale;

        /// <summary>Number of throttled requests.</summary>
        public long Throttled;
    }

    /// <inheritdoc />
    public void RecordOutcome(string supplierId, IngestStatus status)
    {
        var counters = _counters.GetOrAdd(supplierId, _ => new Counters());
        lock (counters.Gate)
        {
            switch (status)
            {
                case IngestStatus.Created: counters.Created++; break;
                case IngestStatus.Updated: counters.Updated++; break;
                case IngestStatus.Unchanged: counters.Unchanged++; break;
                case IngestStatus.Stale: counters.Stale++; break;
                default: throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown ingest status.");
            }
        }
    }

    /// <inheritdoc />
    public void RecordThrottled(string supplierId)
    {
        var counters = _counters.GetOrAdd(supplierId, _ => new Counters());
        lock (counters.Gate)
        {
            counters.Throttled++;
        }
    }

    /// <inheritdoc />
    public SupplierStats Get(string supplierId)
    {
        if (!_counters.TryGetValue(supplierId, out var counters))
        {
            return new SupplierStats(supplierId, 0, 0, new IngestBreakdown(0, 0, 0, 0));
        }

        lock (counters.Gate)
        {
            var ingested = counters.Created + counters.Updated + counters.Unchanged;
            return new SupplierStats(
                supplierId,
                ingested,
                counters.Throttled,
                new IngestBreakdown(counters.Created, counters.Updated, counters.Unchanged, counters.Stale));
        }
    }
}
