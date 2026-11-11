using System.Net.Http.Json;

namespace Mize.E2ETests;

/// <summary>Builds request bodies exactly as a supplier would send them (camelCase JSON, UTC timestamps).</summary>
public static class Payload
{
    /// <summary>Builds a valid reservation body; override any detail via the optional arguments.</summary>
    public static JsonContent Reservation(
        string supplierId,
        string reservationId,
        decimal price = 450.00m,
        string roomId = "room-42",
        string checkIn = "2026-08-01T00:00:00Z",
        string checkOut = "2026-08-03T00:00:00Z",
        string updatedAtUtc = "2026-07-01T10:15:00Z") =>
        JsonContent.Create(new
        {
            supplierId,
            reservationId,
            roomId,
            checkIn,
            checkOut,
            price,
            updatedAtUtc
        });

    /// <summary>Wraps raw text as an application/json body, for sending malformed payloads.</summary>
    public static StringContent Raw(string json) => new(json, System.Text.Encoding.UTF8, "application/json");
}
