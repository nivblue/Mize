namespace Mize.Api.Data;

/// <summary>
/// Every SQL statement used by the application, in one place. Dates are stored as ISO-8601 UTC text and
/// price as invariant-culture decimal text, because SQLite has no native datetime or decimal type and this
/// keeps values exactly round-trippable.
/// </summary>
public static class SqlQueries
{
    /// <summary>Creates the Reservations table if absent; the primary key makes a reservation unique per supplier.</summary>
    public const string CreateReservationsTable = """
        CREATE TABLE IF NOT EXISTS Reservations (
            SupplierId    TEXT NOT NULL,
            ReservationId TEXT NOT NULL,
            RoomId        TEXT NOT NULL,
            CheckIn       TEXT NOT NULL,
            CheckOut      TEXT NOT NULL,
            Price         TEXT NOT NULL,
            UpdatedAtUtc  TEXT NOT NULL,
            PRIMARY KEY (SupplierId, ReservationId)
        );
        """;

    /// <summary>Fetches one reservation by its (SupplierId, ReservationId) key.</summary>
    public const string SelectReservation = """
        SELECT SupplierId, ReservationId, RoomId, CheckIn, CheckOut, Price, UpdatedAtUtc
        FROM Reservations
        WHERE SupplierId = @SupplierId AND ReservationId = @ReservationId;
        """;

    /// <summary>Inserts a brand-new reservation (fails on a duplicate key).</summary>
    public const string InsertReservation = """
        INSERT INTO Reservations (SupplierId, ReservationId, RoomId, CheckIn, CheckOut, Price, UpdatedAtUtc)
        VALUES (@SupplierId, @ReservationId, @RoomId, @CheckIn, @CheckOut, @Price, @UpdatedAtUtc);
        """;

    /// <summary>Overwrites every detail of an existing reservation, located by its key.</summary>
    public const string UpdateReservation = """
        UPDATE Reservations
        SET RoomId = @RoomId,
            CheckIn = @CheckIn,
            CheckOut = @CheckOut,
            Price = @Price,
            UpdatedAtUtc = @UpdatedAtUtc
        WHERE SupplierId = @SupplierId AND ReservationId = @ReservationId;
        """;
}
