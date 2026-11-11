using Microsoft.Data.Sqlite;
using Mize.Api.Data;
using Mize.Api.Models;
using Mize.Tests.Helpers;

namespace Mize.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="ReservationRepository"/>. They run against a real temp SQLite file
/// (the SQL itself is part of the unit being verified), created fresh per test.
/// </summary>
public class ReservationRepositoryTests : IAsyncLifetime, IDisposable
{
    private readonly TempDatabase _db = new();
    private ReservationRepository _repo = null!;

    /// <summary>Creates the schema and the repository before each test.</summary>
    public async Task InitializeAsync()
    {
        await _db.InitializeSchemaAsync();
        _repo = new ReservationRepository(_db.ConnectionFactory);
    }

    /// <summary>Nothing to do asynchronously after a test; cleanup happens in <see cref="Dispose"/>.</summary>
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Deletes the temp database.</summary>
    public void Dispose() => _db.Dispose();

    /// <summary>Looking up a missing key returns null.</summary>
    [Fact]
    public async Task Get_Missing_ReturnsNull()
    {
        Assert.Null(await _repo.GetAsync("s", "nope", CancellationToken.None));
    }

    /// <summary>An inserted reservation is read back with every field (decimal and UTC instants) intact.</summary>
    [Fact]
    public async Task Insert_ThenGet_RoundTripsAllFields()
    {
        var reservation = TestData.Reservation(price: 450.75m);

        await _repo.InsertAsync(reservation, CancellationToken.None);
        var loaded = await _repo.GetAsync("supplier-1", "res-1", CancellationToken.None);

        Assert.Equal(reservation, loaded);
        Assert.Equal(TimeSpan.Zero, loaded!.CheckIn.Offset);
    }

    /// <summary>Update replaces the details of the existing row without creating a new one.</summary>
    [Fact]
    public async Task Update_ChangesStoredDetails()
    {
        await _repo.InsertAsync(TestData.Reservation(price: 100m), CancellationToken.None);

        await _repo.UpdateAsync(TestData.Reservation(price: 250m, roomId: "room-9"), CancellationToken.None);
        var loaded = await _repo.GetAsync("supplier-1", "res-1", CancellationToken.None);

        Assert.Equal(250m, loaded!.Price);
        Assert.Equal("room-9", loaded.RoomId);
    }

    /// <summary>Updating a reservation that does not exist is an error.</summary>
    [Fact]
    public async Task Update_Missing_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _repo.UpdateAsync(TestData.Reservation(), CancellationToken.None));
    }

    /// <summary>Inserting the same (supplier, reservation) twice violates the primary key.</summary>
    [Fact]
    public async Task Insert_DuplicateKey_Throws()
    {
        await _repo.InsertAsync(TestData.Reservation(), CancellationToken.None);

        await Assert.ThrowsAsync<SqliteException>(
            () => _repo.InsertAsync(TestData.Reservation(), CancellationToken.None));
    }

    /// <summary>The same reservationId is allowed for different suppliers.</summary>
    [Fact]
    public async Task Insert_SameReservationIdForDifferentSuppliers_StoresBoth()
    {
        await _repo.InsertAsync(TestData.Reservation(supplierId: "a", price: 1m), CancellationToken.None);
        await _repo.InsertAsync(TestData.Reservation(supplierId: "b", price: 2m), CancellationToken.None);

        Assert.Equal(1m, (await _repo.GetAsync("a", "res-1", CancellationToken.None))!.Price);
        Assert.Equal(2m, (await _repo.GetAsync("b", "res-1", CancellationToken.None))!.Price);
    }

    /// <summary>Ids containing quotes are handled safely through parameters (no SQL injection).</summary>
    [Fact]
    public async Task Queries_AreParameterised()
    {
        var nasty = TestData.Reservation(reservationId: "x'; DROP TABLE Reservations;--");

        await _repo.InsertAsync(nasty, CancellationToken.None);

        Assert.Equal(nasty, await _repo.GetAsync("supplier-1", nasty.ReservationId, CancellationToken.None));
    }
}
