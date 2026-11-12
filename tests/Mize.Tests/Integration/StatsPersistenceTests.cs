using System.Net;
using System.Net.Http.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Mize.Api.Data;
using Mize.Api.Models;
using Mize.Tests.Helpers;

namespace Mize.Tests.Integration;

/// <summary>
/// Integration tests of the statistics cache over a real SQLite file: SQL is the source of truth, memory is a cache that
/// is read once, kept exact while running, and flushed periodically and on graceful shutdown.
/// </summary>
public class StatsPersistenceTests : IDisposable
{
    private const string IngestUrl = "/api/reservations/ingest";
    private readonly TempDatabase _db = new();

    /// <summary>The saved counters of one supplier, as stored in the SupplierStats table.</summary>
    private sealed class SavedRow
    {
        /// <summary>Column Created.</summary>
        public long Created { get; set; }

        /// <summary>Column Updated.</summary>
        public long Updated { get; set; }

        /// <summary>Column Unchanged.</summary>
        public long Unchanged { get; set; }

        /// <summary>Column Throttled.</summary>
        public long Throttled { get; set; }
    }

    /// <summary>Deletes the temp database.</summary>
    public void Dispose() => _db.Dispose();

    /// <summary>Reads the saved counters of a supplier straight from the file, or null when no row exists.</summary>
    private async Task<SavedRow?> SavedAsync(string supplierId)
    {
        await _db.InitializeSchemaAsync();
        await using var connection = await _db.ConnectionFactory.OpenConnectionAsync(CancellationToken.None);
        return await connection.QuerySingleOrDefaultAsync<SavedRow>(
            "SELECT Created, Updated, Unchanged, Throttled FROM SupplierStats WHERE SupplierId = @supplierId;", new { supplierId });
    }

    /// <summary>Counts the reservations stored for a supplier straight from the file.</summary>
    private async Task<long> RowsAsync(string supplierId)
    {
        await using var connection = await _db.ConnectionFactory.OpenConnectionAsync(CancellationToken.None);
        return await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Reservations WHERE SupplierId = @supplierId;", new { supplierId });
    }

    /// <summary>Fetches the stats of a supplier through the API.</summary>
    private static async Task<SupplierStats> StatsAsync(HttpClient client, string supplierId = "supplier-1") =>
        (await client.GetFromJsonAsync<SupplierStats>($"/api/reservations/stats/{supplierId}"))!;

    /// <summary>
    /// A graceful stop flushes everything; the next start loads exactly that: ingested from the rows, the counters (including
    /// throttled) from the saved totals.
    /// </summary>
    [Fact]
    public async Task GracefulStop_SavesTheStatistics_AndTheNextStartSeesThem()
    {
        using (var first = new MizeApiFactory(_db.FilePath) { MaxRequests = 5 })
        using (var client = first.CreateClient())
        {
            await client.PostAsync(IngestUrl, TestData.Json(TestData.Request(reservationId: "a")));                  // created
            await client.PostAsync(IngestUrl, TestData.Json(TestData.Request(reservationId: "b")));                  // created
            await client.PostAsync(IngestUrl, TestData.Json(TestData.Request(reservationId: "c")));                  // created
            await client.PostAsync(IngestUrl, TestData.Json(TestData.Request(reservationId: "a")));                  // duplicate
            await client.PostAsync(IngestUrl, TestData.Json(TestData.Request(reservationId: "b", price: 9m)));       // update
            await client.PostAsync(IngestUrl, TestData.Json(TestData.Request(reservationId: "d")));                  // throttled
            await client.PostAsync(IngestUrl, TestData.Json(TestData.Request(reservationId: "e")));                  // throttled
            Assert.Null(await SavedAsync("supplier-1"));   // the 5-second interval has not elapsed: nothing saved yet
        }

        var saved = await SavedAsync("supplier-1");
        Assert.NotNull(saved);
        Assert.Equal((3L, 1L, 1L, 2L), (saved!.Created, saved.Updated, saved.Unchanged, saved.Throttled));

        using var second = new MizeApiFactory(_db.FilePath);
        using var client2 = second.CreateClient();
        var stats = await StatsAsync(client2);
        Assert.Equal(new SupplierStats("supplier-1", 3, 2, new IngestBreakdown(3, 1, 1)), stats);
    }

    /// <summary>After a restart new activity continues from the saved totals, and a duplicate is still not an ingested reservation.</summary>
    [Fact]
    public async Task AfterRestart_CountersContinue_AndDuplicatesStayOutOfIngested()
    {
        using (var first = new MizeApiFactory(_db.FilePath))
        using (var client = first.CreateClient())
        {
            await client.PostAsync(IngestUrl, TestData.Json(TestData.Request()));
        }

        using var second = new MizeApiFactory(_db.FilePath);
        using var client2 = second.CreateClient();
        await client2.PostAsync(IngestUrl, TestData.Json(TestData.Request()));            // duplicate of the stored one
        var stats = await StatsAsync(client2);

        Assert.Equal(1, stats.Ingested);
        Assert.Equal(new IngestBreakdown(1, 0, 1), stats.Breakdown);
    }

    /// <summary>The periodic flush writes to the file while the API keeps running.</summary>
    [Fact]
    public async Task PeriodicFlush_WritesToTheFile_WhileRunning()
    {
        using var factory = new MizeApiFactory(_db.FilePath) { MaxRequests = 1, StatsFlushIntervalSeconds = 0.1 };
        using var client = factory.CreateClient();
        await client.PostAsync(IngestUrl, TestData.Json(TestData.Request(reservationId: "a")));
        await client.PostAsync(IngestUrl, TestData.Json(TestData.Request(reservationId: "b")));   // throttled

        SavedRow? saved = null;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while ((saved = await SavedAsync("supplier-1")) is null || saved.Throttled == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "the counters were not flushed within 5 seconds");
            await Task.Delay(50);
        }

        Assert.Equal((1L, 0L, 0L, 1L), (saved.Created, saved.Updated, saved.Unchanged, saved.Throttled));
    }

    /// <summary>
    /// "Ingested" is the number of rows, not a saved counter: reservations that exist in SQL (for example written before the
    /// counters existed, or by a crashed run) are counted even though no counters were ever saved.
    /// </summary>
    [Fact]
    public async Task ExistingRowsWithoutSavedCounters_AreStillCountedAsIngested()
    {
        await _db.InitializeSchemaAsync();
        var repository = new ReservationRepository(_db.ConnectionFactory);
        await repository.InsertAsync(TestData.Reservation(reservationId: "old-1"), CancellationToken.None);
        await repository.InsertAsync(TestData.Reservation(reservationId: "old-2"), CancellationToken.None);

        using var factory = new MizeApiFactory(_db.FilePath);
        using var client = factory.CreateClient();
        var stats = await StatsAsync(client);

        Assert.Equal(2, stats.Ingested);
        Assert.Equal(new IngestBreakdown(0, 0, 0), stats.Breakdown);
    }

    /// <summary>
    /// The cache stays exact under load: many parallel ingests of different reservations interleaved with stats reads (the first
    /// read races the first writes); the final count equals the rows in the file, and what a reader sees never goes backwards.
    /// </summary>
    [Fact]
    public async Task CachedCount_MatchesSql_AfterConcurrentIngestsAndReads()
    {
        using var factory = new MizeApiFactory(_db.FilePath);
        using var client = factory.CreateClient();
        const int reservations = 60;
        using var stop = new CancellationTokenSource();

        var reader = Task.Run(async () =>
        {
            long last = 0;
            while (!stop.IsCancellationRequested)
            {
                var ingested = (await StatsAsync(client, "p")).Ingested;
                Assert.True(ingested >= last, $"ingested went backwards: {last} -> {ingested}");
                last = ingested;
            }
        });
        var posts = Enumerable.Range(0, reservations).Select(i => Task.Run(async () =>
        {
            var response = await client.PostAsync(IngestUrl, TestData.Json(TestData.Request(supplierId: "p", reservationId: $"r{i}")));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }));
        await Task.WhenAll(posts);
        stop.Cancel();
        await reader;

        var stats = await StatsAsync(client, "p");
        Assert.Equal(reservations, stats.Ingested);
        Assert.Equal(await RowsAsync("p"), stats.Ingested);
        Assert.Equal(reservations, stats.Breakdown.Created);
    }

    /// <summary>
    /// Rule 3 end to end: an insert that commits and then fails (a 500) leaves a row the cache never recorded; the cache must
    /// notice and report the real count, not the stale one.
    /// </summary>
    [Fact]
    public async Task AmbiguousInsertFailure_DoesNotLeaveAStaleRowCount()
    {
        await _db.InitializeSchemaAsync();
        var flaky = new CommitThenThrowRepository(new ReservationRepository(_db.ConnectionFactory));
        using var factory = new MizeApiFactory(_db.FilePath) { RepositoryOverride = flaky };
        using var client = factory.CreateClient();

        await client.PostAsync(IngestUrl, TestData.Json(TestData.Request(reservationId: "ok-1")));
        Assert.Equal(1, (await StatsAsync(client)).Ingested);                         // the cache is now loaded

        flaky.ThrowAfterInsertCommit = true;
        var failed = await client.PostAsync(IngestUrl, TestData.Json(TestData.Request(reservationId: "ok-2")));
        flaky.ThrowAfterInsertCommit = false;
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);

        var stats = await StatsAsync(client);
        Assert.Equal(2, await RowsAsync("supplier-1"));                              // the row really exists
        Assert.Equal(2, stats.Ingested);                                              // and the cache reports it
    }
}
