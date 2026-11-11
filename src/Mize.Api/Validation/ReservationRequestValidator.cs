using Mize.Api.Models;

namespace Mize.Api.Validation;

/// <summary>
/// Strict business validation for <see cref="ReservationRequest"/> (non-empty ids, UTC timestamps,
/// check-out after check-in, non-negative price). Structural problems such as missing JSON properties
/// are caught earlier by model binding.
/// </summary>
public sealed class ReservationRequestValidator
{
    /// <summary>
    /// Validates a request.
    /// </summary>
    /// <param name="request">The request to validate.</param>
    /// <returns>Errors keyed by camelCase property name; empty when the request is valid.</returns>
    public IReadOnlyDictionary<string, string[]> Validate(ReservationRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        RequireText(errors, "supplierId", request.SupplierId);
        RequireText(errors, "reservationId", request.ReservationId);
        RequireText(errors, "roomId", request.RoomId);

        RequireUtc(errors, "checkIn", request.CheckIn);
        RequireUtc(errors, "checkOut", request.CheckOut);
        RequireUtc(errors, "updatedAtUtc", request.UpdatedAtUtc);

        if (request.CheckOut <= request.CheckIn)
        {
            errors["checkOut"] = new[] { "checkOut must be after checkIn." };
        }

        if (request.Price < 0)
        {
            errors["price"] = new[] { "price must be zero or greater." };
        }

        return errors;
    }

    /// <summary>Adds an error when <paramref name="value"/> is null, empty or whitespace.</summary>
    private static void RequireText(Dictionary<string, string[]> errors, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors[name] = new[] { $"{name} is required and must not be empty." };
        }
    }

    /// <summary>Adds an error when the timestamp is not expressed in UTC (offset other than +00:00 / 'Z').</summary>
    private static void RequireUtc(Dictionary<string, string[]> errors, string name, DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            errors[name] = new[] { $"{name} must be a UTC timestamp (offset +00:00 or 'Z')." };
        }
    }
}
