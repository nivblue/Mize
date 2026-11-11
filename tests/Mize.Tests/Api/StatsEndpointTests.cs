using System.Net;
using System.Net.Http.Json;
using Mize.Api.Models;
using Mize.Tests.Helpers;

namespace Mize.Tests.Api;

/// <summary>API tests for <c>GET /api/reservations/stats/{supplierId}</c>.</summary>
public class StatsEndpointTests : IDisposable
{
    private const string IngestUrl = "/api/reservations/ingest";
    private readonly MizeApiFactory _factory = new() { RepositoryOverride = new InMemoryReservationRepository(), MaxRequests = 2 };
    private readonly HttpClient _client;

    /// <summary>Starts the API with a rate limit of 2 so throttles are easy to produce.</summary>
    public StatsEndpointTests()
    {
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

    /// <summary>The breakdown separates created, updated and unchanged outcomes (an older timestamp with new details counts as updated).</summary>
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

        Assert.Equal(4, stats.Ingested);
        Assert.Equal(new IngestBreakdown(1, 2, 1), stats.Breakdown);
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
