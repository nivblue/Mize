using System.Collections.Concurrent;
using Mize.Api.Data;
using Mize.Api.Models;

namespace Mize.Tests.Helpers;

/// <summary>Dictionary-backed repository used by API tests so they exercise HTTP behaviour without a database.</summary>
public sealed class InMemoryReservationRepository : IReservationRepository
{
    private readonly ConcurrentDictionary<(string, string), Reservation> _store = new();

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
