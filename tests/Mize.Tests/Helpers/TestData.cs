using System.Net.Http.Json;
using System.Text.Json;
using Mize.Api.Models;

namespace Mize.Tests.Helpers;

/// <summary>Factory methods for test payloads; defaults match the sample from the task description.</summary>
public static class TestData
{
    /// <summary>Default check-in used by <see cref="Request"/>.</summary>
    public static readonly DateTimeOffset CheckIn = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Default check-out used by <see cref="Request"/>.</summary>
    public static readonly DateTimeOffset CheckOut = new(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Default updatedAtUtc used by <see cref="Request"/>.</summary>
    public static readonly DateTimeOffset UpdatedAt = new(2026, 7, 1, 10, 15, 0, TimeSpan.Zero);

    /// <summary>Builds a valid request; override any detail via the optional arguments.</summary>
    public static ReservationRequest Request(
        string supplierId = "supplier-1",
        string reservationId = "res-1",
        string roomId = "room-1",
        DateTimeOffset? checkIn = null,
        DateTimeOffset? checkOut = null,
        decimal price = 450.00m,
        DateTimeOffset? updatedAtUtc = null) => new()
    {
        SupplierId = supplierId,
        ReservationId = reservationId,
        RoomId = roomId,
        CheckIn = checkIn ?? CheckIn,
        CheckOut = checkOut ?? CheckOut,
        Price = price,
        UpdatedAtUtc = updatedAtUtc ?? UpdatedAt
    };

    /// <summary>Builds the stored form of <see cref="Request"/> with the same defaults.</summary>
    public static Reservation Reservation(
        string supplierId = "supplier-1",
        string reservationId = "res-1",
        string roomId = "room-1",
        decimal price = 450.00m,
        DateTimeOffset? updatedAtUtc = null) =>
        Mize.Api.Models.Reservation.FromRequest(Request(supplierId, reservationId, roomId, price: price, updatedAtUtc: updatedAtUtc));

    /// <summary>Serialises a request to a JSON HTTP body using camelCase names, as a real supplier would.</summary>
    public static JsonContent Json(ReservationRequest request) =>
        JsonContent.Create(request, options: new JsonSerializerOptions(JsonSerializerDefaults.Web));

    /// <summary>Creates a raw JSON body, for sending invalid or hand-crafted payloads.</summary>
    public static StringContent RawJson(string json) =>
        new(json, System.Text.Encoding.UTF8, "application/json");
}
