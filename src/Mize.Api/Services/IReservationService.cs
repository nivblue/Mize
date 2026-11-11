using Mize.Api.Models;

namespace Mize.Api.Services;

/// <summary>Business rules for ingesting reservations.</summary>
public interface IReservationService
{
    /// <summary>
    /// Ingests a validated reservation: inserts it if new, ignores exact duplicates (nothing is written), and
    /// otherwise updates the stored record.
    /// </summary>
    /// <param name="request">The validated request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What happened to the reservation.</returns>
    Task<IngestStatus> IngestAsync(ReservationRequest request, CancellationToken cancellationToken);
}
