using Mize.Api.Data;
using Mize.Api.Stats;
using Mize.Tests.Helpers;

namespace Mize.Tests.Unit;

/// <summary>Unit tests for <see cref="StatsRepository"/> against a real temporary SQLite file.</summary>
public class StatsRepositoryTests : IAsyncLifetime, IDisposable
{
    private readonly TempDatabase _db = new();
    private StatsRepository _stats = null!;
    private ReservationRepository _reservations = null!;

    /// <summary>Creates the schema and the repositories before each test.</summary>
    public async Task InitializeAsync()
    {
        await _db.InitializeSchemaAsync();
        _stats = new StatsRepository(_db.ConnectionFactory);
        _reservations = new ReservationRepository(_db.ConnectionFactory);
    }

    /// <summary>Nothing to do asynchronously after a test; cleanup happens in <see cref="Dispose"/>.</summary>
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Deletes the temp database.</summary>
    public void Dispose() => _db.Dispose();

    /// <summary>A supplier with nothing stored has zero rows and no saved counters.</summary>
    [Fact]
    public async Task Load_UnknownSupplier_ReturnsZeroRows_AndNoCounters()
    {
        var stored = await _stats.LoadAsync("nobody", CancellationToken.None);

        Assert.Equal(0, stored.Rows);
        Assert.Null(stored.Counters);
    }

    /// <summary>The row count is per supplier and counts reservations, not requests.</summary>
    [Fact]
    public async Task Load_CountsOnlyThatSuppliersReservations()
    {
        await _reservations.InsertAsync(TestData.Reservation(supplierId: "a", reservationId: "1"), CancellationToken.None);
        await _reservations.InsertAsync(TestData.Reservation(supplierId: "a", reservationId: "2"), CancellationToken.None);
        await _reservations.InsertAsync(TestData.Reservation(supplierId: "b", reservationId: "1"), CancellationToken.None);
        await _reservations.UpdateAsync(TestData.Reservation(supplierId: "a", reservationId: "1", price: 9m), CancellationToken.None);

        Assert.Equal(2, (await _stats.LoadAsync("a", CancellationToken.None)).Rows);
        Assert.Equal(1, (await _stats.LoadAsync("b", CancellationToken.None)).Rows);
    }

    /// <summary>Adding creates the row the first time and then accumulates (never overwrites).</summary>
    [Fact]
    public async Task Add_CreatesThenAccumulates()
    {
        await _stats.AddAsync(new Dictionary<string, StatsCounters> { ["a"] = new(1, 0, 0, 2) }, CancellationToken.None);
        await _stats.AddAsync(new Dictionary<string, StatsCounters> { ["a"] = new(0, 1, 3, 1) }, CancellationToken.None);

        var stored = await _stats.LoadAsync("a", CancellationToken.None);

        Assert.Equal(new StatsCounters(1, 1, 3, 3), stored.Counters);
    }

    /// <summary>Several suppliers are written by one call, each to its own row.</summary>
    [Fact]
    public async Task Add_WritesEverySupplierInOneCall()
    {
        await _stats.AddAsync(new Dictionary<string, StatsCounters>
        {
            ["a"] = new(1, 0, 0, 0),
            ["b"] = new(0, 0, 0, 5)
        }, CancellationToken.None);

        Assert.Equal(new StatsCounters(1, 0, 0, 0), (await _stats.LoadAsync("a", CancellationToken.None)).Counters);
        Assert.Equal(new StatsCounters(0, 0, 0, 5), (await _stats.LoadAsync("b", CancellationToken.None)).Counters);
    }

    /// <summary>An empty batch does nothing.</summary>
    [Fact]
    public async Task Add_Empty_DoesNothing()
    {
        await _stats.AddAsync(new Dictionary<string, StatsCounters>(), CancellationToken.None);

        Assert.Null((await _stats.LoadAsync("a", CancellationToken.None)).Counters);
    }

    /// <summary>Rows and saved counters come back together in one read.</summary>
    [Fact]
    public async Task Load_ReturnsRowsAndCountersTogether()
    {
        await _reservations.InsertAsync(TestData.Reservation(), CancellationToken.None);
        await _stats.AddAsync(new Dictionary<string, StatsCounters> { ["supplier-1"] = new(1, 2, 3, 4) }, CancellationToken.None);

        var stored = await _stats.LoadAsync("supplier-1", CancellationToken.None);

        Assert.Equal(1, stored.Rows);
        Assert.Equal(new StatsCounters(1, 2, 3, 4), stored.Counters);
    }

    /// <summary>A supplier id with quotes is handled safely through parameters.</summary>
    [Fact]
    public async Task Queries_AreParameterised()
    {
        var nasty = "x'; DROP TABLE SupplierStats;--";

        await _stats.AddAsync(new Dictionary<string, StatsCounters> { [nasty] = new(1, 0, 0, 0) }, CancellationToken.None);

        Assert.Equal(new StatsCounters(1, 0, 0, 0), (await _stats.LoadAsync(nasty, CancellationToken.None)).Counters);
    }
}
