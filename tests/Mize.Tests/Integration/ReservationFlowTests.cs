using System.Net;
using System.Net.Http.Json;
using Dapper;
using Mize.Api.Data;
using Mize.Api.Models;
using Mize.Tests.Helpers;

namespace Mize.Tests.Integration;

/// <summary>
/// End-to-end tests: the real API, real Dapper repository and a real SQLite file. Each test checks the
/// HTTP responses AND what actually ended up in the database.
/// </summary>
public class ReservationFlowTests : IDisposable
{
    private const string IngestUrl = "/api/reservations/ingest";
    private readonly MizeApiFactory _factory = new();
    private readonly HttpClient _client;

    /// <summary>Starts the full stack with the real default rate limit (100 per 60 s).</summary>
    public ReservationFlowTests()
    {
        _client = _factory.CreateClient();
    }

    /// <summary>Stops the API and deletes its database.</summary>
    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    /// <summary>Reads a row straight from the database, bypassing the API.</summary>
    private Task<Reservation?> LoadAsync(string supplierId = "supplier-1", string reservationId = "res-1") =>
        new ReservationRepository(new SqliteConnectionFactory($"Data Source={_factory.DatabasePath}"))
            .GetAsync(supplierId, reservationId, CancellationToken.None);

    /// <summary>Counts all rows in the Reservations table.</summary>
    private async Task<int> CountRowsAsync()
    {
        await using var connection = await new SqliteConnectionFactory($"Data Source={_factory.DatabasePath}")
            .OpenConnectionAsync(CancellationToken.None);
        return await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Reservations;");
    }

    /// <summary>A new reservation is persisted exactly as sent.</summary>
    [Fact]
    public async Task Ingest_New_IsPersisted()
    {
        var response = await _client.PostAsync(IngestUrl, TestData.Json(TestData.Request()));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(TestData.Reservation(), await LoadAsync());
    }

    /// <summary>The full lifecycle: create → duplicate (unchanged) → change (updated) → older (stale), checking the DB at each step.</summary>
    [Fact]
    public async Task Ingest_FullLifecycle_BehavesAsSpecified()
    {
        // 1. created
        Assert.Equal(HttpStatusCode.Created, (await _client.PostAsync(IngestUrl, TestData.Json(TestData.Request(price: 450m)))).StatusCode);
        Assert.Equal(450m, (await LoadAsync())!.Price);

        // 2. identical duplicate -> unchanged, still one row
        var duplicate = await _client.PostAsync(IngestUrl, TestData.Json(TestData.Request(price: 450m)));
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        Assert.Contains("unchanged", await duplicate.Content.ReadAsStringAsync());
        Assert.Equal(1, await CountRowsAsync());

        // 3. changed details -> updated in place
        var changed = await _client.PostAsync(IngestUrl, TestData.Json(
            TestData.Request(price: 520.5m, roomId: "room-2", updatedAtUtc: TestData.UpdatedAt.AddHours(2))));
        Assert.Contains("updated", await changed.Content.ReadAsStringAsync());
        var stored = (await LoadAsync())!;
        Assert.Equal(520.5m, stored.Price);
        Assert.Equal("room-2", stored.RoomId);
        Assert.Equal(1, await CountRowsAsync());

        // 4. older update -> stale, database untouched, WARN logged
        var stale = await _client.PostAsync(IngestUrl, TestData.Json(
            TestData.Request(price: 1m, updatedAtUtc: TestData.UpdatedAt.AddHours(-3))));
        Assert.Contains("stale", await stale.Content.ReadAsStringAsync());
        Assert.Equal(520.5m, (await LoadAsync())!.Price);
        Assert.Contains(_factory.Logs.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning && e.Message.Contains("out-of-order"));

        // 5. stats reflect all four requests
        var stats = (await _client.GetFromJsonAsync<SupplierStats>("/api/reservations/stats/supplier-1"))!;
        Assert.Equal(3, stats.Ingested);
        Assert.Equal(0, stats.Throttled);
        Assert.Equal(new IngestBreakdown(1, 1, 1, 1), stats.Breakdown);
    }

    /// <summary>Two suppliers can use the same reservationId without interfering.</summary>
    [Fact]
    public async Task Ingest_SameReservationIdDifferentSuppliers_AreSeparateRows()
    {
        await _client.PostAsync(IngestUrl, TestData.Json(TestData.Request(supplierId: "a", price: 1m)));
        await _client.PostAsync(IngestUrl, TestData.Json(TestData.Request(supplierId: "b", price: 2m)));

        Assert.Equal(1m, (await LoadAsync("a"))!.Price);
        Assert.Equal(2m, (await LoadAsync("b"))!.Price);
    }

    /// <summary>The 101st request within 60 seconds is rejected with 429, 100 rows exist, and the stats show 100 vs 1.</summary>
    [Fact]
    public async Task Ingest_101stRequestInWindow_IsThrottled_AndStatsShowIt()
    {
        for (var i = 0; i < 100; i++)
        {
            var ok = await _client.PostAsync(IngestUrl, TestData.Json(TestData.Request(reservationId: $"r{i}")));
            Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        }

        var throttled = await _client.PostAsync(IngestUrl, TestData.Json(TestData.Request(reservationId: "r100")));

        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.Equal(100, await CountRowsAsync());
        Assert.Null(await LoadAsync(reservationId: "r100"));
        var stats = (await _client.GetFromJsonAsync<SupplierStats>("/api/reservations/stats/supplier-1"))!;
        Assert.Equal(100, stats.Ingested);
        Assert.Equal(1, stats.Throttled);
    }

    /// <summary>Once the 60 seconds pass, the blocked supplier can ingest again.</summary>
    [Fact]
    public async Task Ingest_AfterWindowClears_SupplierIsAcceptedAgain()
    {
        for (var i = 0; i < 101; i++)
        {
            await _client.PostAsync(IngestUrl, TestData.Json(TestData.Request(reservationId: $"r{i}")));
        }

        _factory.Time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await _client.PostAsync(IngestUrl, TestData.Json(TestData.Request(reservationId: "x")))).StatusCode);

        _factory.Time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(HttpStatusCode.Created, (await _client.PostAsync(IngestUrl, TestData.Json(TestData.Request(reservationId: "x")))).StatusCode);
    }

    /// <summary>Parallel identical requests result in exactly one row, one 201 and the rest 200 (no duplicate-key 500s).</summary>
    [Fact]
    public async Task Ingest_ParallelDuplicates_CreateExactlyOneRow()
    {
        var responses = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => _client.PostAsync(IngestUrl, TestData.Json(TestData.Request()))));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(19, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, await CountRowsAsync());
    }

    /// <summary>Data survives an API restart (a new host on the same database file) and is recognised as unchanged.</summary>
    [Fact]
    public async Task Ingest_AfterRestart_ExistingDataIsStillThere()
    {
        await _client.PostAsync(IngestUrl, TestData.Json(TestData.Request()));

        using var restarted = new MizeApiFactory(_factory.DatabasePath);
        using var client = restarted.CreateClient();
        var response = await client.PostAsync(IngestUrl, TestData.Json(TestData.Request()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("unchanged", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, await CountRowsAsync());
    }

    /// <summary>Invalid payloads never reach the database.</summary>
    [Fact]
    public async Task Ingest_Invalid_IsNotPersisted()
    {
        var response = await _client.PostAsync(IngestUrl, TestData.Json(TestData.Request(price: -5m)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await CountRowsAsync());
    }
}
