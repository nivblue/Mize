using System.Collections.Concurrent;
using Mize.Api.Data;
using Mize.Api.Models;
using Mize.Api.Services;

namespace Mize.Api.Stats;

/// <summary>
/// In-memory cache of per-supplier statistics with SQL as the source of truth. For a supplier it holds the number of
/// reservation rows and the four request counters. The database is read once per supplier (on the first read), and from
/// then on everything is served from memory; counters are written back in batches by <see cref="FlushAsync"/>.
/// <para>
/// The rules that keep it correct for a single instance that is the only writer of the database:
/// (1) the first load happens while holding the write gate, so no ingest can be half-done during the load;
/// (2) the row count is incremented only for <c>Created</c>, only after the insert has committed, inside the gate;
/// (3) after an ambiguous insert failure the row count is dropped and reloaded (<see cref="InvalidateRows"/>);
/// (4) an increment for a supplier whose row count is not loaded is not applied (the later load already includes the row);
/// (5) entries are created only for suppliers with data or activity, never by a read of an unknown supplier;
/// (6) a flush and a load never overlap, and a flush adds deltas (it never overwrites), so totals are exact.
/// </para>
/// Statistics reset only to what was last flushed if the process dies without a graceful stop.
/// </summary>
public sealed class SupplierStatsCache : ISupplierStatsTracker, IStatsFlusher
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly IStatsRepository _repository;
    private readonly IWriteGate _writeGate;

    // Serialises flushes and loads (never held by the request path), so a load cannot read the database while a flush
    // is between "saved" and "accounted for" and end up counting an increment twice or not at all.
    private readonly SemaphoreSlim _ioLock = new(1, 1);

    /// <summary>The cached state of one supplier; <see cref="Gate"/> guards every other field.</summary>
    private sealed class Entry
    {
        /// <summary>Lock making reads and updates of this entry atomic.</summary>
        public readonly object Gate = new();

        /// <summary>True when <see cref="Rows"/> holds the reservation count and is being maintained.</summary>
        public bool RowsLoaded;

        /// <summary>Number of reservations stored for the supplier (valid only when <see cref="RowsLoaded"/>).</summary>
        public long Rows;

        /// <summary>True when <see cref="Base"/> holds the counters saved in the database.</summary>
        public bool CountersLoaded;

        /// <summary>The counters already saved in the database (valid only when <see cref="CountersLoaded"/>).</summary>
        public StatsCounters Base = StatsCounters.Zero;

        /// <summary>Increments not yet saved; the reported total is <see cref="Base"/> plus this.</summary>
        public StatsCounters Unflushed = StatsCounters.Zero;
    }

    /// <summary>Creates the cache.</summary>
    /// <param name="repository">Where the statistics are read from and flushed to.</param>
    /// <param name="writeGate">The gate shared with the ingest logic.</param>
    public SupplierStatsCache(IStatsRepository repository, IWriteGate writeGate)
    {
        _repository = repository;
        _writeGate = writeGate;
    }

    /// <inheritdoc />
    public void RecordOutcome(string supplierId, IngestStatus status)
    {
        var delta = status switch
        {
            IngestStatus.Created => new StatsCounters(1, 0, 0, 0),
            IngestStatus.Updated => new StatsCounters(0, 1, 0, 0),
            IngestStatus.Unchanged => new StatsCounters(0, 0, 1, 0),
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown ingest status.")
        };

        var entry = _entries.GetOrAdd(supplierId, _ => new Entry());
        lock (entry.Gate)
        {
            entry.Unflushed += delta;

            // Rule 2 and 4: only a created reservation changes the row count, and only if the count is being maintained.
            if (status == IngestStatus.Created && entry.RowsLoaded)
            {
                entry.Rows++;
            }
        }
    }

    /// <inheritdoc />
    public void RecordThrottled(string supplierId)
    {
        var entry = _entries.GetOrAdd(supplierId, _ => new Entry());
        lock (entry.Gate)
        {
            entry.Unflushed += new StatsCounters(0, 0, 0, 1);
        }
    }

    /// <inheritdoc />
    public void InvalidateRows(string supplierId)
    {
        if (_entries.TryGetValue(supplierId, out var entry))
        {
            lock (entry.Gate)
            {
                entry.RowsLoaded = false;
            }
        }
    }

    /// <inheritdoc />
    public async Task<SupplierStats> GetAsync(string supplierId, CancellationToken cancellationToken)
    {
        // Fast path: everything is in memory.
        if (_entries.TryGetValue(supplierId, out var entry))
        {
            lock (entry.Gate)
            {
                if (entry.RowsLoaded && entry.CountersLoaded)
                {
                    return Snapshot(supplierId, entry);
                }
            }
        }

        return await LoadAsync(supplierId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        await _ioLock.WaitAsync(cancellationToken);
        try
        {
            // 1. Copy the pending increments (new increments keep arriving while we write).
            var pending = new Dictionary<string, StatsCounters>();
            foreach (var (supplierId, entry) in _entries)
            {
                lock (entry.Gate)
                {
                    if (!entry.Unflushed.IsZero)
                    {
                        pending[supplierId] = entry.Unflushed;
                    }
                }
            }

            if (pending.Count == 0)
            {
                return;
            }

            // 2. Save them. If this throws, nothing was changed in memory and the next flush retries.
            await _repository.AddAsync(pending, cancellationToken);

            // 3. Move exactly the saved amounts from "unflushed" to "saved"; the reported total does not change.
            foreach (var (supplierId, delta) in pending)
            {
                var entry = _entries[supplierId];
                lock (entry.Gate)
                {
                    entry.Unflushed -= delta;
                    if (entry.CountersLoaded)
                    {
                        entry.Base += delta;
                    }
                }
            }
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <summary>Reads a supplier from the database while holding the write gate and the I/O lock, and caches it.</summary>
    private async Task<SupplierStats> LoadAsync(string supplierId, CancellationToken cancellationToken)
    {
        using var gate = await _writeGate.EnterAsync(cancellationToken);   // rule 1: no ingest is mid-flight
        await _ioLock.WaitAsync(cancellationToken);                        // rule 6: no flush is mid-flight
        try
        {
            var stored = await _repository.LoadAsync(supplierId, cancellationToken);

            // Rule 5: do not create an entry for a supplier that has no data and no activity.
            if (stored.Rows == 0 && stored.Counters is null && !_entries.ContainsKey(supplierId))
            {
                return new SupplierStats(supplierId, 0, 0, new IngestBreakdown(0, 0, 0));
            }

            var entry = _entries.GetOrAdd(supplierId, _ => new Entry());
            lock (entry.Gate)
            {
                entry.Rows = stored.Rows;
                entry.RowsLoaded = true;
                entry.Base = stored.Counters ?? StatsCounters.Zero;
                entry.CountersLoaded = true;
                return Snapshot(supplierId, entry);
            }
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <summary>Builds the response from an entry whose parts are loaded; the caller holds the entry lock.</summary>
    private static SupplierStats Snapshot(string supplierId, Entry entry)
    {
        var total = entry.Base + entry.Unflushed;
        return new SupplierStats(
            supplierId,
            entry.Rows,
            total.Throttled,
            new IngestBreakdown(total.Created, total.Updated, total.Unchanged));
    }
}
