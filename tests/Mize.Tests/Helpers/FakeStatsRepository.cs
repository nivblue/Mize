using Mize.Api.Data;
using Mize.Api.Stats;

namespace Mize.Tests.Helpers;

/// <summary>
/// In-memory <see cref="IStatsRepository"/> for tests: the row count of a supplier comes from a settable function, the
/// saved counters live in a dictionary, and every call is counted so tests can prove how often the "database" was used.
/// </summary>
public sealed class FakeStatsRepository : IStatsRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<string, StatsCounters> _counters = new();
    private int _loadCount;
    private int _addCount;

    /// <summary>Supplies the reservation count of a supplier; default is zero for everybody.</summary>
    public Func<string, long> RowsProvider { get; set; } = _ => 0;

    /// <summary>When true, the next <see cref="AddAsync"/> throws (and the flag resets), simulating a failed flush.</summary>
    public bool FailNextAdd { get; set; }

    /// <summary>Number of <see cref="LoadAsync"/> calls so far.</summary>
    public int LoadCount => Volatile.Read(ref _loadCount);

    /// <summary>Number of successful <see cref="AddAsync"/> calls so far.</summary>
    public int AddCount => Volatile.Read(ref _addCount);

    /// <summary>The deltas of every successful <see cref="AddAsync"/> call, in order.</summary>
    public List<IReadOnlyDictionary<string, StatsCounters>> AddCalls { get; } = new();

    /// <summary>Pre-saves counters for a supplier, as if an earlier run had flushed them.</summary>
    public void Seed(string supplierId, StatsCounters counters)
    {
        lock (_gate)
        {
            _counters[supplierId] = counters;
        }
    }

    /// <summary>The counters currently saved for a supplier (zero when none).</summary>
    public StatsCounters SavedFor(string supplierId)
    {
        lock (_gate)
        {
            return _counters.TryGetValue(supplierId, out var c) ? c : StatsCounters.Zero;
        }
    }

    /// <inheritdoc />
    public Task<StoredSupplierStats> LoadAsync(string supplierId, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _loadCount);
        lock (_gate)
        {
            _counters.TryGetValue(supplierId, out var saved);
            return Task.FromResult(new StoredSupplierStats(RowsProvider(supplierId), saved));
        }
    }

    /// <inheritdoc />
    public Task AddAsync(IReadOnlyDictionary<string, StatsCounters> deltas, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (FailNextAdd)
            {
                FailNextAdd = false;
                throw new InvalidOperationException("db down");
            }

            foreach (var (supplierId, delta) in deltas)
            {
                _counters[supplierId] = (_counters.TryGetValue(supplierId, out var c) ? c : StatsCounters.Zero) + delta;
            }

            AddCalls.Add(new Dictionary<string, StatsCounters>(deltas));
            Interlocked.Increment(ref _addCount);
        }

        return Task.CompletedTask;
    }
}
