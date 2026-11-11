using Dapper;
using Mize.Api.Data;
using Mize.Tests.Helpers;

namespace Mize.Tests.Unit;

/// <summary>Unit tests for <see cref="DatabaseInitializer"/>.</summary>
public class DatabaseInitializerTests
{
    /// <summary>Counts tables named Reservations in the given database.</summary>
    private static async Task<int> CountReservationTablesAsync(TempDatabase db)
    {
        await using var connection = await db.ConnectionFactory.OpenConnectionAsync(CancellationToken.None);
        return await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'Reservations';");
    }

    /// <summary>Initialising an empty database creates the Reservations table.</summary>
    [Fact]
    public async Task Initialize_CreatesReservationsTable()
    {
        using var db = new TempDatabase();

        await new DatabaseInitializer(db.ConnectionFactory).InitializeAsync();

        Assert.Equal(1, await CountReservationTablesAsync(db));
    }

    /// <summary>Running it again is harmless and keeps existing data.</summary>
    [Fact]
    public async Task Initialize_IsIdempotent_AndKeepsData()
    {
        using var db = new TempDatabase();
        var initializer = new DatabaseInitializer(db.ConnectionFactory);
        await initializer.InitializeAsync();
        await new ReservationRepository(db.ConnectionFactory).InsertAsync(TestData.Reservation(), CancellationToken.None);

        await initializer.InitializeAsync();

        Assert.NotNull(await new ReservationRepository(db.ConnectionFactory).GetAsync("supplier-1", "res-1", CancellationToken.None));
    }
}
