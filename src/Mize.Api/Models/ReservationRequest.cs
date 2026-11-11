namespace Mize.Api.Models;

/// <summary>
/// Payload sent by a supplier to <c>POST /api/reservations/ingest</c>.
/// All members are <c>required</c>, so a missing JSON property is rejected with HTTP 400 at binding time.
/// </summary>
public sealed record ReservationRequest
{
    /// <summary>Identifier of the supplier sending the reservation (also the rate-limit key).</summary>
    public required string SupplierId { get; init; }

    /// <summary>Supplier-side reservation identifier; unique per supplier.</summary>
    public required string ReservationId { get; init; }

    /// <summary>Identifier of the reserved room.</summary>
    public required string RoomId { get; init; }

    /// <summary>Check-in instant (must be UTC).</summary>
    public required DateTimeOffset CheckIn { get; init; }

    /// <summary>Check-out instant (must be UTC and after <see cref="CheckIn"/>).</summary>
    public required DateTimeOffset CheckOut { get; init; }

    /// <summary>Total price of the reservation (must be zero or positive).</summary>
    public required decimal Price { get; init; }

    /// <summary>When the supplier last modified the reservation (must be UTC); used to detect stale updates.</summary>
    public required DateTimeOffset UpdatedAtUtc { get; init; }
}
