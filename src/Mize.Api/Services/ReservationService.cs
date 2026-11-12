using Mize.Api.Data;
using Mize.Api.Models;
using Mize.Api.Stats;

namespace Mize.Api.Services;

/// <summary>
/// Implements the ingest rules. Logging levels: created / updated / unchanged = Information,
/// out-of-order update (older updatedAtUtc, still applied) = Warning. Must be a singleton: it shares the write gate
/// with the statistics cache. The statistics outcome is recorded here, inside the gate and right after the write has
/// committed, which is what keeps the cached reservation count exact.
/// </summary>
public sealed class ReservationService : IReservationService
{
    private readonly IReservationRepository _repository;
    private readonly IWriteGate _gate;
    private readonly ISupplierStatsTracker _stats;
    private readonly ILogger<ReservationService> _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="repository">Reservation persistence.</param>
    /// <param name="gate">Serialises read-decide-write so two concurrent requests for the same reservation cannot both insert (primary-key violation) or race on update. Adequate for one process over one SQLite file.</param>
    /// <param name="stats">Statistics; receives each outcome.</param>
    /// <param name="logger">Logger.</param>
    public ReservationService(IReservationRepository repository, IWriteGate gate, ISupplierStatsTracker stats, ILogger<ReservationService> logger)
    {
        _repository = repository;
        _gate = gate;
        _stats = stats;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IngestStatus> IngestAsync(ReservationRequest request, CancellationToken cancellationToken)
    {
        var incoming = Reservation.FromRequest(request);

        using var gate = await _gate.EnterAsync(cancellationToken);

        var existing = await _repository.GetAsync(incoming.SupplierId, incoming.ReservationId, cancellationToken);

        if (existing is null)
        {
            try
            {
                await _repository.InsertAsync(incoming, cancellationToken);
            }
            catch
            {
                // The insert may or may not have committed (for example the connection dropped after the commit),
                // so the cached reservation count can no longer be trusted: reload it on the next read.
                _stats.InvalidateRows(incoming.SupplierId);
                throw;
            }

            // Right after the commit, inside the gate, with no await in between.
            _stats.RecordOutcome(incoming.SupplierId, IngestStatus.Created);
            _logger.LogInformation(
                "Created reservation {ReservationId} for supplier {SupplierId}",
                incoming.ReservationId, incoming.SupplierId);
            return IngestStatus.Created;
        }

        // Record equality: every detail (room, dates, price, updatedAtUtc) is identical.
        if (existing == incoming)
        {
            _stats.RecordOutcome(incoming.SupplierId, IngestStatus.Unchanged);
            _logger.LogInformation(
                "Reservation {ReservationId} for supplier {SupplierId} unchanged; nothing written",
                incoming.ReservationId, incoming.SupplierId);
            return IngestStatus.Unchanged;
        }

        await _repository.UpdateAsync(incoming, cancellationToken);
        _stats.RecordOutcome(incoming.SupplierId, IngestStatus.Updated);

        // The specification says different details always update the record, so an older updatedAtUtc is still
        // applied. It is unusual (a late or replayed message), so it is logged at Warning.
        if (incoming.UpdatedAtUtc < existing.UpdatedAtUtc)
        {
            _logger.LogWarning(
                "Applied out-of-order update for reservation {ReservationId} of supplier {SupplierId}: " +
                "incoming updatedAtUtc {IncomingUpdatedAtUtc:O} is older than the previously stored {StoredUpdatedAtUtc:O}",
                incoming.ReservationId, incoming.SupplierId, incoming.UpdatedAtUtc, existing.UpdatedAtUtc);
        }
        else
        {
            _logger.LogInformation(
                "Updated reservation {ReservationId} for supplier {SupplierId}",
                incoming.ReservationId, incoming.SupplierId);
        }

        return IngestStatus.Updated;
    }
}
