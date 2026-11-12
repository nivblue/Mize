using Mize.Api.Data;
using Mize.Api.Models;

namespace Mize.Tests.Helpers;

/// <summary>
/// Decorator over a real repository that can simulate an AMBIGUOUS failure: the insert is committed and then the call
/// throws anyway (as if the connection dropped right after the commit). Used to prove the statistics cache does not
/// keep a stale reservation count after such a failure.
/// </summary>
public sealed class CommitThenThrowRepository : IReservationRepository
{
    private readonly IReservationRepository _inner;

    /// <summary>Wraps a real repository.</summary>
    public CommitThenThrowRepository(IReservationRepository inner)
    {
        _inner = inner;
    }

    /// <summary>While true, <see cref="InsertAsync"/> commits and then throws.</summary>
    public bool ThrowAfterInsertCommit { get; set; }

    /// <inheritdoc />
    public Task<Reservation?> GetAsync(string supplierId, string reservationId, CancellationToken cancellationToken) =>
        _inner.GetAsync(supplierId, reservationId, cancellationToken);

    /// <inheritdoc />
    public async Task InsertAsync(Reservation reservation, CancellationToken cancellationToken)
    {
        await _inner.InsertAsync(reservation, cancellationToken);
        if (ThrowAfterInsertCommit)
        {
            throw new InvalidOperationException("connection dropped after the commit");
        }
    }

    /// <inheritdoc />
    public Task UpdateAsync(Reservation reservation, CancellationToken cancellationToken) =>
        _inner.UpdateAsync(reservation, cancellationToken);
}
