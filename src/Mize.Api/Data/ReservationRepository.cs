using System.Globalization;
using Dapper;
using Mize.Api.Models;

namespace Mize.Api.Data;

/// <summary>Dapper-based <see cref="IReservationRepository"/> over any ADO.NET connection (SQLite here).</summary>
public sealed class ReservationRepository : IReservationRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    /// <summary>
    /// Flat, all-text mirror of a table row. Dapper maps columns to these properties; conversion to and from
    /// <see cref="Reservation"/> happens here so dates and decimals round-trip exactly.
    /// </summary>
    private sealed class ReservationRow
    {
        /// <summary>Column SupplierId.</summary>
        public string SupplierId { get; set; } = "";

        /// <summary>Column ReservationId.</summary>
        public string ReservationId { get; set; } = "";

        /// <summary>Column RoomId.</summary>
        public string RoomId { get; set; } = "";

        /// <summary>Column CheckIn (ISO-8601 UTC).</summary>
        public string CheckIn { get; set; } = "";

        /// <summary>Column CheckOut (ISO-8601 UTC).</summary>
        public string CheckOut { get; set; } = "";

        /// <summary>Column Price (invariant decimal text).</summary>
        public string Price { get; set; } = "";

        /// <summary>Column UpdatedAtUtc (ISO-8601 UTC).</summary>
        public string UpdatedAtUtc { get; set; } = "";

        /// <summary>Converts a domain reservation to its stored text form.</summary>
        public static ReservationRow From(Reservation r) => new()
        {
            SupplierId = r.SupplierId,
            ReservationId = r.ReservationId,
            RoomId = r.RoomId,
            CheckIn = FormatDate(r.CheckIn),
            CheckOut = FormatDate(r.CheckOut),
            Price = r.Price.ToString(CultureInfo.InvariantCulture),
            UpdatedAtUtc = FormatDate(r.UpdatedAtUtc)
        };

        /// <summary>Converts the stored text form back to a domain reservation.</summary>
        public Reservation ToReservation() => new(
            SupplierId,
            ReservationId,
            RoomId,
            ParseDate(CheckIn),
            ParseDate(CheckOut),
            decimal.Parse(Price, NumberStyles.Number, CultureInfo.InvariantCulture),
            ParseDate(UpdatedAtUtc));

        /// <summary>Formats an instant as round-trippable ISO-8601 UTC text.</summary>
        private static string FormatDate(DateTimeOffset value) =>
            value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

        /// <summary>Parses text produced by <see cref="FormatDate"/> back into a UTC instant.</summary>
        private static DateTimeOffset ParseDate(string text) =>
            DateTimeOffset.Parse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    }

    /// <summary>Creates the repository.</summary>
    /// <param name="connectionFactory">Source of open connections; one is opened per operation.</param>
    public ReservationRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    /// <inheritdoc />
    public async Task<Reservation?> GetAsync(string supplierId, string reservationId, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<ReservationRow>(new CommandDefinition(
            SqlQueries.SelectReservation,
            new { SupplierId = supplierId, ReservationId = reservationId },
            cancellationToken: cancellationToken));
        return row?.ToReservation();
    }

    /// <inheritdoc />
    public async Task InsertAsync(Reservation reservation, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            SqlQueries.InsertReservation,
            ReservationRow.From(reservation),
            cancellationToken: cancellationToken));
    }

    /// <inheritdoc />
    public async Task UpdateAsync(Reservation reservation, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            SqlQueries.UpdateReservation,
            ReservationRow.From(reservation),
            cancellationToken: cancellationToken));

        if (affected == 0)
        {
            throw new InvalidOperationException(
                $"Reservation '{reservation.ReservationId}' of supplier '{reservation.SupplierId}' does not exist.");
        }
    }
}
