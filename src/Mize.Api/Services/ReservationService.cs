using Mize.Api.Data;
using Mize.Api.Models;

namespace Mize.Api.Services;

/// <summary>
/// Implements the ingest rules. Logging levels: created / updated / unchanged = Information,
/// stale update = Warning. Must be registered as a singleton because it owns the write gate.
/// </summary>
public sealed class ReservationService : IReservationService
{
    // Serialises read-decide-write so two concurrent requests for the same reservation cannot both
    // insert (primary-key violation) or race on update. Adequate for one process over one SQLite file.
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly IReservationRepository _repository;
    private readonly ILogger<ReservationService> _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="repository">Reservation persistence.</param>
    /// <param name="logger">Logger.</param>
    public ReservationService(IReservationRepository repository, ILogger<ReservationService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IngestStatus> IngestAsync(ReservationRequest request, CancellationToken cancellationToken)
    {
        var incoming = Reservation.FromRequest(request);

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var existing = await _repository.GetAsync(incoming.SupplierId, incoming.ReservationId, cancellationToken);

            if (existing is null)
            {
                await _repository.InsertAsync(incoming, cancellationToken);
                _logger.LogInformation(
                    "Created reservation {ReservationId} for supplier {SupplierId}",
                    incoming.ReservationId, incoming.SupplierId);
                return IngestStatus.Created;
            }

            // Record equality: every detail (room, dates, price, updatedAtUtc) is identical.
            if (existing == incoming)
            {
                _logger.LogInformation(
                    "Reservation {ReservationId} for supplier {SupplierId} unchanged; nothing written",
                    incoming.ReservationId, incoming.SupplierId);
                return IngestStatus.Unchanged;
            }

            if (incoming.UpdatedAtUtc < existing.UpdatedAtUtc)
            {
                _logger.LogWarning(
                    "Ignoring out-of-order update for reservation {ReservationId} of supplier {SupplierId}: " +
                    "incoming updatedAtUtc {IncomingUpdatedAtUtc:O} is older than stored {StoredUpdatedAtUtc:O}",
                    incoming.ReservationId, incoming.SupplierId, incoming.UpdatedAtUtc, existing.UpdatedAtUtc);
                return IngestStatus.Stale;
            }

            await _repository.UpdateAsync(incoming, cancellationToken);
            _logger.LogInformation(
                "Updated reservation {ReservationId} for supplier {SupplierId}",
                incoming.ReservationId, incoming.SupplierId);
            return IngestStatus.Updated;
        }
        finally
        {
            _writeGate.Release();
        }
    }
}
