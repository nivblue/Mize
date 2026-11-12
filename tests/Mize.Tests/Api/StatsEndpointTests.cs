using System.Net;
using System.Net.Http.Json;
using Mize.Api.Models;
using Mize.Api.Stats;
using Mize.Tests.Helpers;

namespace Mize.Tests.Api;

/// <summary>API tests for <c>GET /api/reservations/stats/{supplierId}</c>.</summary>
public class StatsEndpointTests : IDisposable
{
    private const string IngestUrl = "/api/reservations/ingest";
    private readonly InMemoryReservationRepository _repo = new();
    private readonly MizeApiFactory _factory;
    private readonly HttpClient _client;

    /// <summary>Starts the API with a rate limit of 2 so throttles are easy to produce.</summary>
    public StatsEndpointTests()
    {
        _factory = new MizeApiFactory { RepositoryOverride = _repo, MaxRequests = 2 };
        _client = _factory.CreateClient();
    }

    /// <summary>Stops the API.</summary>
    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    /// <summary>Fetches and deserialises the stats for a supplier.</summary>
    private async Task<SupplierStats> GetStatsAsync(string supplierId)
    {
        var response = await _client.GetAsync($"/api/reservations/stats/{supplierId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SupplierStats>())!;
    }

    /// <summary>A supplier that never sent anything gets 200 with zeros (not 404).</summary>
    [Fact]
    public async Task Get_UnknownSupplier_ReturnsZeros()
    {
        var stats = await GetStatsAsync("ghost");

        Assert.Equal("ghost", stats.SupplierId);
        Assert.Equal(0, stats.Ingested);
        Assert.Equal(0, stats.Throttled);
    }

    /// <summary>Ingested and throttled counts reflect what happened: 2 accepted, 3 throttled.</summary>
    [Fact]
    public async Task Get_AfterIngestsAndThrottles_ReportsBothCounts()
    {
        for (var i = 0; i < 5; i++)
        {
            await _client.PostAsync(IngestUrl, TestData.Json(TestData.Request(reservationId: $"r{i}")));
        }

        var stats = await GetStatsAsync("supplier-1");

        Assert.Equal(2, stats.Ingested);
        Assert.Equal(3, stats.Throttled);
        Assert.Equal(2, stats.Breakdown.Created);
    }

    /// <summary>
    /// The breakdown separates created, updated and unchanged outcomes (an older timestamp with new details counts as
    /// updated), while "ingested" counts reservations: four requests on one reservation are ONE ingested reservation.
    /// </summary>
    [Fact]
    public async Task Get_Breakdown_SeparatesOutcomes()
    {
        var second = new MizeApiFactory { RepositoryOverride = new InMemoryReservationRepository() };
        using var client = second.CreateClient();
        await client.PostAsync(IngestUrl, TestData.Json(TestData.Request(price: 1m)));                                                     // created
        await client.PostAsync(IngestUrl, TestData.Json(TestData.Request(price: 1m)));                                                     // unchanged
        await client.PostAsync(IngestUrl, TestData.Json(TestData.Request(price: 2m, updatedAtUtc: TestData.UpdatedAt.AddHours(1))));       // updated
        await client.PostAsync(IngestUrl, TestData.Json(TestData.Request(price: 3m, updatedAtUtc: TestData.UpdatedAt.AddHours(-1))));      // updated (older timestamp)

        var stats = (await client.GetFromJsonAsync<SupplierStats>("/api/reservations/stats/supplier-1"))!;
        second.Dispose();

        Assert.Equal(1, stats.Ingested);
        Assert.Equal(new IngestBreakdown(1, 2, 1), stats.Breakdown);
    }

    /// <summary>Exact duplicates and updates never increase "ingested"; only new reservations do.</summary>
    [Fact]
    public async Task Get_DuplicatesAndUpdates_DoNotIncreaseIngested()
    {
        using var factory = new MizeApiFactory { RepositoryOverride = new InMemoryReservationRepository() };
        using var client = factory.CreateClient();
        await client.PostAsync(IngestUrl, TestData.Json(TestData.Request(reservationId: "a")));                  // created
        await client.PostAsync(IngestUrl, TestData.Json(TestData.Request(reservationId: "a")));                  // duplicate
        await client.PostAsync(IngestUrl, TestData.Json(TestData.Request(reservationId: "a", price: 9m)));       // update
        await client.PostAsync(IngestUrl, TestData.Json(TestData.Request(reservationId: "b")));                  // created

        var stats = (await client.GetFromJsonAsync<SupplierStats>("/api/reservations/stats/supplier-1"))!;

        Assert.Equal(2, stats.Ingested);
        Assert.Equal(new IngestBreakdown(2, 1, 1), stats.Breakdown);
    }

    /// <summary>The database is read once per supplier; later reads, and later ingests, are served and applied in memory.</summary>
    [Fact]
    public async Task Get_ReadsTheDatabaseOnlyOnce_ThenServesFromTheCache()
    {
        var repo = new InMemoryReservationRepository();
        using var factory = new MizeApiFactory { RepositoryOverride = repo };
        using var client = factory.CreateClient();
        await client.PostAsync(IngestUrl, TestData.Json(TestData.Request(reservationId: "r0")));

        Assert.Equal(1, (await client.GetFromJsonAsync<SupplierStats>("/api/reservations/stats/supplier-1"))!.Ingested);
        Assert.Equal(1, repo.StatsLoadCount);

        await client.PostAsync(IngestUrl, TestData.Json(TestData.Request(reservationId: "r1")));
        await client.PostAsync(IngestUrl, TestData.Json(TestData.Request(reservationId: "r2")));
        var again = (await client.GetFromJsonAsync<SupplierStats>("/api/reservations/stats/supplier-1"))!;
        await client.GetAsync("/api/reservations/stats/supplier-1");

        Assert.Equal(3, again.Ingested);               // the new rows are reflected ...
        Assert.Equal(1, repo.StatsLoadCount);          // ... without another database read
    }

    /// <summary>A supplier without data is not cached: reading it never creates state, so random ids cannot grow memory.</summary>
    [Fact]
    public async Task Get_UnknownSupplier_IsNotCached()
    {
        var repo = new InMemoryReservationRepository();
        using var factory = new MizeApiFactory { RepositoryOverride = repo };
        using var client = factory.CreateClient();

        await client.GetAsync("/api/reservations/stats/ghost");
        await client.GetAsync("/api/reservations/stats/ghost");

        Assert.Equal(2, repo.StatsLoadCount);
    }

    /// <summary>Throttle counts accumulate in memory and are reported without an extra database read.</summary>
    [Fact]
    public async Task Get_AfterThrottling_ReportsThrottles_WithASingleDatabaseRead()
    {
        for (var i = 0; i < 3; i++)
        {
            await _client.PostAsync(IngestUrl, TestData.Json(TestData.Request(reservationId: $"r{i}")));   // 2 accepted, 1 throttled
        }

        var stats = await GetStatsAsync("supplier-1");
        await GetStatsAsync("supplier-1");

        Assert.Equal(1, stats.Throttled);
        Assert.Equal(1, _repo.StatsLoadCount);
    }

    /// <summary>When the host stops gracefully, the final flush saves the counters.</summary>
    [Fact]
    public async Task Stop_SavesTheCounters_ToTheDatabase()
    {
        var repo = new InMemoryReservationRepository();
        using (var factory = new MizeApiFactory { RepositoryOverride = repo, MaxRequests = 3 })
        using (var client = factory.CreateClient())
        {
            await client.PostAsync(IngestUrl, TestData.Json(TestData.Request()));                // created
            await client.PostAsync(IngestUrl, TestData.Json(TestData.Request()));                // duplicate
            await client.PostAsync(IngestUrl, TestData.Json(TestData.Request(price: 5m)));       // update
            await client.PostAsync(IngestUrl, TestData.Json(TestData.Request(price: 6m)));       // throttled
            Assert.Equal(StatsCounters.Zero, repo.SavedStatsFor("supplier-1"));                  // nothing saved yet (interval is 5 s)
        }

        Assert.Equal(new StatsCounters(1, 1, 1, 1), repo.SavedStatsFor("supplier-1"));
    }

    /// <summary>The periodic flush saves the counters while the API keeps running, at the configured interval.</summary>
    [Fact]
    public async Task PeriodicFlush_SavesTheCounters_WhileRunning()
    {
        var repo = new InMemoryReservationRepository();
        using var factory = new MizeApiFactory { RepositoryOverride = repo, StatsFlushIntervalSeconds = 0.05 };
        using var client = factory.CreateClient();

        await client.PostAsync(IngestUrl, TestData.Json(TestData.Request()));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (repo.SavedStatsFor("supplier-1").Created == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "the counters were not flushed within 5 seconds");
            await Task.Delay(20);
        }

        Assert.Equal(new StatsCounters(1, 0, 0, 0), repo.SavedStatsFor("supplier-1"));
    }

    /// <summary>Stats are kept per supplier.</summary>
    [Fact]
    public async Task Get_IsPerSupplier()
    {
        await _client.PostAsync(IngestUrl, TestData.Json(TestData.Request(supplierId: "a")));

        Assert.Equal(1, (await GetStatsAsync("a")).Ingested);
        Assert.Equal(0, (await GetStatsAsync("b")).Ingested);
    }

    /// <summary>Rejected invalid requests are not counted as ingested or throttled.</summary>
    [Fact]
    public async Task Get_InvalidRequests_AreNotCounted()
    {
        await _client.PostAsync(IngestUrl, TestData.Json(TestData.Request(price: -1m)));

        var stats = await GetStatsAsync("supplier-1");

        Assert.Equal(0, stats.Ingested);
        Assert.Equal(0, stats.Throttled);
    }

    /// <summary>The stats route only accepts GET; POST returns 405.</summary>
    [Fact]
    public async Task Post_OnStatsRoute_Returns405()
    {
        var response = await _client.PostAsync("/api/reservations/stats/s", TestData.RawJson("{}"));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    /// <summary>A stats URL without a supplier id does not exist: 404.</summary>
    [Fact]
    public async Task Get_WithoutSupplierId_Returns404()
    {
        var response = await _client.GetAsync("/api/reservations/stats/");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
