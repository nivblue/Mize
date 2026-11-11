using Mize.Api.Models;

namespace Mize.Api.Data;

/// <summary>Persistence operations for reservations.</summary>
public interface IReservationRepository
{
    /// <summary>Loads a reservation by key.</summary>
    /// <param name="supplierId">Supplier id.</param>
    /// <param name="reservationId">Reservation id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The reservation, or <c>null</c> when it does not exist.</returns>
    Task<Reservation?> GetAsync(string supplierId, string reservationId, CancellationToken cancellationToken);

    /// <summary>Inserts a new reservation; throws if the key already exists.</summary>
    /// <param name="reservation">The reservation to store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task InsertAsync(Reservation reservation, CancellationToken cancellationToken);

    /// <summary>Overwrites an existing reservation; throws if it does not exist.</summary>
    /// <param name="reservation">The new details (key identifies the row).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task UpdateAsync(Reservation reservation, CancellationToken cancellationToken);
}
