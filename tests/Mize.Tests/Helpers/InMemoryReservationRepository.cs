using System.Collections.Concurrent;
using Mize.Api.Data;
using Mize.Api.Models;
using Mize.Api.Stats;

namespace Mize.Tests.Helpers;

/// <summary>
/// Dictionary-backed repository used by API tests so they exercise HTTP behaviour without a database. It also acts as the
/// statistics repository (the row count is the number of reservations it holds), so stats reads stay consistent with it.
/// </summary>
public sealed class InMemoryReservationRepository : IReservationRepository, IStatsRepository
{
    private readonly ConcurrentDictionary<(string, string), Reservation> _store = new();
    private readonly FakeStatsRepository _stats = new();

    /// <summary>Creates the repository and points the statistics' row count at the stored reservations.</summary>
    public InMemoryReservationRepository()
    {
        _stats.RowsProvider = supplierId => _store.Keys.LongCount(key => key.Item1 == supplierId);
    }

    /// <summary>How many times the statistics were read from the "database" (proves the cache is used).</summary>
    public int StatsLoadCount => _stats.LoadCount;

    /// <summary>The counters saved so far for a supplier by flushes.</summary>
    public StatsCounters SavedStatsFor(string supplierId) => _stats.SavedFor(supplierId);

    /// <inheritdoc />
    public Task<StoredSupplierStats> LoadAsync(string supplierId, CancellationToken cancellationToken) =>
        _stats.LoadAsync(supplierId, cancellationToken);

    /// <inheritdoc />
    public Task AddAsync(IReadOnlyDictionary<string, StatsCounters> deltas, CancellationToken cancellationToken) =>
        _stats.AddAsync(deltas, cancellationToken);

    /// <summary>Number of Insert/Update calls made (to prove "unchanged" writes nothing).</summary>
    public int WriteCount;

    /// <summary>Number of reservations currently stored.</summary>
    public int Count => _store.Count;

    /// <inheritdoc />
    public Task<Reservation?> GetAsync(string supplierId, string reservationId, CancellationToken cancellationToken) =>
        Task.FromResult(_store.TryGetValue((supplierId, reservationId), out var r) ? r : null);

    /// <inheritdoc />
    public Task InsertAsync(Reservation reservation, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref WriteCount);
        if (!_store.TryAdd((reservation.SupplierId, reservation.ReservationId), reservation))
        {
            throw new InvalidOperationException("Duplicate key.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UpdateAsync(Reservation reservation, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref WriteCount);
        _store[(reservation.SupplierId, reservation.ReservationId)] = reservation;
        return Task.CompletedTask;
    }
}
