namespace Mize.Api.Models;

/// <summary>
/// A reservation as stored in the database. Identity is (<see cref="SupplierId"/>, <see cref="ReservationId"/>).
/// Being a record, two instances are equal when every detail is equal, which is how "same details" is detected.
/// </summary>
/// <param name="SupplierId">Supplier that owns the reservation.</param>
/// <param name="ReservationId">Supplier-side reservation identifier.</param>
/// <param name="RoomId">Reserved room.</param>
/// <param name="CheckIn">Check-in instant (UTC).</param>
/// <param name="CheckOut">Check-out instant (UTC).</param>
/// <param name="Price">Total price.</param>
/// <param name="UpdatedAtUtc">Supplier-side last-modified instant (UTC).</param>
public sealed record Reservation(
    string SupplierId,
    string ReservationId,
    string RoomId,
    DateTimeOffset CheckIn,
    DateTimeOffset CheckOut,
    decimal Price,
    DateTimeOffset UpdatedAtUtc)
{
    /// <summary>
    /// Converts an incoming request into a storable reservation, normalising every timestamp to UTC.
    /// </summary>
    /// <param name="request">The validated request.</param>
    /// <returns>The equivalent <see cref="Reservation"/>.</returns>
    public static Reservation FromRequest(ReservationRequest request) => new(
        request.SupplierId,
        request.ReservationId,
        request.RoomId,
        request.CheckIn.ToUniversalTime(),
        request.CheckOut.ToUniversalTime(),
        request.Price,
        request.UpdatedAtUtc.ToUniversalTime());
}
