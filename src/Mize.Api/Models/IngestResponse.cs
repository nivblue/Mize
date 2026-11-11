namespace Mize.Api.Models;

/// <summary>Body returned by <c>POST /api/reservations/ingest</c> on success.</summary>
/// <param name="Status">What happened to the reservation (serialised as a camelCase string).</param>
/// <param name="SupplierId">Echo of the supplier id.</param>
/// <param name="ReservationId">Echo of the reservation id.</param>
/// <param name="Message">Human-readable explanation of the outcome.</param>
public sealed record IngestResponse(IngestStatus Status, string SupplierId, string ReservationId, string Message);
