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

    /// <summary>
    /// Creates the table of persisted per-supplier request counters (saved by the periodic flush). The reservation row
    /// count is NOT stored here: it is always read from the Reservations table.
    /// </summary>
    public const string CreateSupplierStatsTable = """
        CREATE TABLE IF NOT EXISTS SupplierStats (
            SupplierId TEXT    PRIMARY KEY,
            Created    INTEGER NOT NULL DEFAULT 0,
            Updated    INTEGER NOT NULL DEFAULT 0,
            Unchanged  INTEGER NOT NULL DEFAULT 0,
            Throttled  INTEGER NOT NULL DEFAULT 0
        );
        """;

    /// <summary>
    /// Reads, as one statement (so one consistent snapshot), a supplier's reservation count (an index range scan of the
    /// primary key) and its saved counters (all NULL when the supplier has no stats row yet).
    /// </summary>
    public const string LoadSupplierStats = """
        SELECT
            (SELECT COUNT(*) FROM Reservations WHERE SupplierId = @SupplierId) AS ReservationCount,
            (SELECT Created   FROM SupplierStats WHERE SupplierId = @SupplierId) AS Created,
            (SELECT Updated   FROM SupplierStats WHERE SupplierId = @SupplierId) AS Updated,
            (SELECT Unchanged FROM SupplierStats WHERE SupplierId = @SupplierId) AS Unchanged,
            (SELECT Throttled FROM SupplierStats WHERE SupplierId = @SupplierId) AS Throttled;
        """;

    /// <summary>
    /// Adds a delta to a supplier's saved counters, creating the row when missing. Adding (not overwriting) is what
    /// makes repeated and concurrent flushes safe.
    /// </summary>
    public const string AddSupplierStatsDelta = """
        INSERT INTO SupplierStats (SupplierId, Created, Updated, Unchanged, Throttled)
        VALUES (@SupplierId, @Created, @Updated, @Unchanged, @Throttled)
        ON CONFLICT (SupplierId) DO UPDATE SET
            Created   = Created   + excluded.Created,
            Updated   = Updated   + excluded.Updated,
            Unchanged = Unchanged + excluded.Unchanged,
            Throttled = Throttled + excluded.Throttled;
        """;
}
