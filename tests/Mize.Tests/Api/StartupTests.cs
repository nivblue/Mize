using Microsoft.AspNetCore.Mvc.Testing;
using System.Net;
using System.Net.Http.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using Mize.Api.Models;
using Mize.Tests.Helpers;

namespace Mize.Tests.Api;

/// <summary>Tests of the application start-up itself (configuration problems must stop the service, not let it run broken).</summary>
public class StartupTests
{
    /// <summary>
    /// With no connection string configured the service refuses to start, with a message naming the missing setting,
    /// instead of running without a database.
    /// </summary>
    [Fact]
    public void Startup_WithoutAConnectionString_FailsFast()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Reservations"] = null })));

        var failure = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Connection string 'Reservations' is not configured", Describe(failure));
    }

    /// <summary>
    /// The production wiring, with NO test overrides: a connection string from configuration is enough for the service to
    /// create its schema (both tables) in that file and answer requests, and ingested data lands in that file.
    /// </summary>
    [Fact]
    public async Task Startup_WithAConnectionString_CreatesTheSchema_AndServesRequests()
    {
        using var db = new TempDatabase();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Reservations"] = db.ConnectionString })));
        using var client = factory.CreateClient();

        var ingest = await client.PostAsync("/api/reservations/ingest", TestData.Json(TestData.Request()));
        var stats = await client.GetAsync("/api/reservations/stats/supplier-1");

        Assert.Equal(HttpStatusCode.Created, ingest.StatusCode);
        Assert.Equal(HttpStatusCode.OK, stats.StatusCode);
        Assert.Equal(1, (await stats.Content.ReadFromJsonAsync<SupplierStats>())!.Ingested);
        await using var connection = await db.ConnectionFactory.OpenConnectionAsync(CancellationToken.None);
        Assert.Equal(2, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('Reservations', 'SupplierStats');"));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Reservations;"));
    }

    /// <summary>Flattens an exception and its inner exceptions into one string to search.</summary>
    private static string Describe(Exception exception) =>
        string.Join(" | ", Enumerable.Range(0, 5).Aggregate(
            new List<Exception> { exception },
            (list, _) => { if (list[^1].InnerException is { } inner) list.Add(inner); return list; }).Select(e => e.Message));
}
