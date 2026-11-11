using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Moq;
using Mize.Api.Data;
using Mize.Api.Models;
using Mize.Tests.Helpers;

namespace Mize.Tests.Api;

/// <summary>
/// API tests for <c>POST /api/reservations/ingest</c>: real HTTP pipeline, in-memory repository, fake clock.
/// Covers success paths and every failure response.
/// </summary>
public class IngestEndpointTests : IDisposable
{
    private const string Url = "/api/reservations/ingest";
    private readonly InMemoryReservationRepository _repo = new();
    private readonly MizeApiFactory _factory;
    private readonly HttpClient _client;

    /// <summary>Starts the API with a small rate limit (3) so throttling is quick to reach.</summary>
    public IngestEndpointTests()
    {
        _factory = new MizeApiFactory { RepositoryOverride = _repo, MaxRequests = 3 };
        _client = _factory.CreateClient();
    }

    /// <summary>Stops the API.</summary>
    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    /// <summary>Reads the JSON response as a generic document for assertions on the wire format.</summary>
    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>());

    // ---------- success ----------

    /// <summary>A new reservation returns 201 with status "created".</summary>
    [Fact]
    public async Task Post_NewReservation_Returns201Created()
    {
        var response = await _client.PostAsync(Url, TestData.Json(TestData.Request()));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal("created", body.GetProperty("status").GetString());
        Assert.Equal("res-1", body.GetProperty("reservationId").GetString());
        Assert.Equal(1, _repo.Count);
    }

    /// <summary>The exact JSON from the task description is accepted.</summary>
    [Fact]
    public async Task Post_SamplePayloadFromSpec_Returns201()
    {
        const string json = """
            {"supplierId":"s1","reservationId":"r1","roomId":"room","checkIn":"2026-08-01T00:00:00Z",
             "checkOut":"2026-08-03T00:00:00Z","price":450.00,"updatedAtUtc":"2026-07-01T10:15:00Z"}
            """;

        var response = await _client.PostAsync(Url, TestData.RawJson(json));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>Re-sending identical details returns 200 "unchanged" with a clear message and writes nothing.</summary>
    [Fact]
    public async Task Post_IdenticalDuplicate_Returns200Unchanged_AndDoesNotWrite()
    {
        await _client.PostAsync(Url, TestData.Json(TestData.Request()));

        var response = await _client.PostAsync(Url, TestData.Json(TestData.Request()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal("unchanged", body.GetProperty("status").GetString());
        Assert.Contains("nothing changed", body.GetProperty("message").GetString());
        Assert.Equal(1, _repo.WriteCount);
    }

    /// <summary>Same id with different details returns 200 "updated" and stores the new details.</summary>
    [Fact]
    public async Task Post_ChangedDetails_Returns200Updated_AndStoresNewDetails()
    {
        await _client.PostAsync(Url, TestData.Json(TestData.Request(price: 450m)));

        var response = await _client.PostAsync(Url, TestData.Json(TestData.Request(price: 500m, updatedAtUtc: TestData.UpdatedAt.AddHours(1))));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("updated", (await ReadJsonAsync(response)).GetProperty("status").GetString());
        Assert.Equal(500m, (await _repo.GetAsync("supplier-1", "res-1", default))!.Price);
    }

    /// <summary>An update with an older updatedAtUtc but different details is still applied (200 "updated") and logs a WARN.</summary>
    [Fact]
    public async Task Post_OlderTimestampWithDifferentDetails_StillUpdates_AndLogsWarning()
    {
        await _client.PostAsync(Url, TestData.Json(TestData.Request(price: 450m)));

        var response = await _client.PostAsync(Url, TestData.Json(TestData.Request(price: 1m, updatedAtUtc: TestData.UpdatedAt.AddHours(-1))));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("updated", (await ReadJsonAsync(response)).GetProperty("status").GetString());
        Assert.Equal(1m, (await _repo.GetAsync("supplier-1", "res-1", default))!.Price);
        Assert.Contains(_factory.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("out-of-order"));
    }

    // ---------- 400 validation failures ----------

    /// <summary>Business-rule violations return 400 with the offending field, are logged at Warning, and store nothing.</summary>
    [Theory]
    [InlineData("""{"supplierId":"","reservationId":"r","roomId":"x","checkIn":"2026-08-01T00:00:00Z","checkOut":"2026-08-03T00:00:00Z","price":1,"updatedAtUtc":"2026-07-01T00:00:00Z"}""", "supplierId")]
    [InlineData("""{"supplierId":"s","reservationId":"r","roomId":"x","checkIn":"2026-08-03T00:00:00Z","checkOut":"2026-08-01T00:00:00Z","price":1,"updatedAtUtc":"2026-07-01T00:00:00Z"}""", "checkOut")]
    [InlineData("""{"supplierId":"s","reservationId":"r","roomId":"x","checkIn":"2026-08-01T00:00:00Z","checkOut":"2026-08-03T00:00:00Z","price":-1,"updatedAtUtc":"2026-07-01T00:00:00Z"}""", "price")]
    [InlineData("""{"supplierId":"s","reservationId":"r","roomId":"x","checkIn":"2026-08-01T00:00:00+02:00","checkOut":"2026-08-03T00:00:00Z","price":1,"updatedAtUtc":"2026-07-01T00:00:00Z"}""", "checkIn")]
    public async Task Post_InvalidBusinessRule_Returns400WithField(string json, string field)
    {
        var response = await _client.PostAsync(Url, TestData.RawJson(json));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await ReadJsonAsync(response)).GetProperty("errors");
        Assert.True(errors.TryGetProperty(field, out _), $"expected an error for '{field}'");
        Assert.Equal(0, _repo.Count);
        Assert.Contains(_factory.Logs.Entries, e => e.Level == LogLevel.Warning);
    }

    /// <summary>A missing required property is rejected at binding time with 400 and a Warning log.</summary>
    [Fact]
    public async Task Post_MissingProperty_Returns400_AndLogsWarning()
    {
        const string json = """{"supplierId":"s","reservationId":"r","checkIn":"2026-08-01T00:00:00Z","checkOut":"2026-08-03T00:00:00Z","price":1,"updatedAtUtc":"2026-07-01T00:00:00Z"}""";

        var response = await _client.PostAsync(Url, TestData.RawJson(json));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(_factory.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("could not be bound"));
    }

    /// <summary>Malformed JSON returns 400.</summary>
    [Fact]
    public async Task Post_MalformedJson_Returns400()
    {
        var response = await _client.PostAsync(Url, TestData.RawJson("{ not json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>A wrongly typed value (price as text) returns 400.</summary>
    [Fact]
    public async Task Post_WrongType_Returns400()
    {
        const string json = """{"supplierId":"s","reservationId":"r","roomId":"x","checkIn":"2026-08-01T00:00:00Z","checkOut":"2026-08-03T00:00:00Z","price":"abc","updatedAtUtc":"2026-07-01T00:00:00Z"}""";

        var response = await _client.PostAsync(Url, TestData.RawJson(json));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>An empty body returns 400.</summary>
    [Fact]
    public async Task Post_EmptyBody_Returns400()
    {
        var response = await _client.PostAsync(Url, TestData.RawJson(""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------- other protocol failures ----------

    /// <summary>A non-JSON content type returns 415.</summary>
    [Fact]
    public async Task Post_NonJsonContentType_Returns415()
    {
        var response = await _client.PostAsync(Url, new StringContent("hello", System.Text.Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    /// <summary>Using GET on the ingest route returns 405.</summary>
    [Fact]
    public async Task Get_OnIngestRoute_Returns405()
    {
        var response = await _client.GetAsync(Url);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    // ---------- 429 throttling ----------

    /// <summary>After the limit is used up, the next request gets 429 with Retry-After and a Warning log, and nothing is stored.</summary>
    [Fact]
    public async Task Post_OverLimit_Returns429WithRetryAfter_AndLogsWarning()
    {
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Created, (await _client.PostAsync(Url, TestData.Json(TestData.Request(reservationId: $"r{i}")))).StatusCode);
        }

        var response = await _client.PostAsync(Url, TestData.Json(TestData.Request(reservationId: "r-extra")));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("60", response.Headers.GetValues("Retry-After").Single());
        Assert.Equal(3, _repo.Count);
        Assert.Contains(_factory.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("Throttled"));
    }

    /// <summary>One supplier being throttled does not affect another supplier.</summary>
    [Fact]
    public async Task Post_ThrottledSupplier_DoesNotAffectOthers()
    {
        for (var i = 0; i < 4; i++)
        {
            await _client.PostAsync(Url, TestData.Json(TestData.Request(supplierId: "noisy", reservationId: $"r{i}")));
        }

        var other = await _client.PostAsync(Url, TestData.Json(TestData.Request(supplierId: "quiet")));

        Assert.Equal(HttpStatusCode.Created, other.StatusCode);
    }

    /// <summary>When the window clears the supplier is accepted again.</summary>
    [Fact]
    public async Task Post_AfterWindowClears_IsAcceptedAgain()
    {
        for (var i = 0; i < 4; i++)
        {
            await _client.PostAsync(Url, TestData.Json(TestData.Request(reservationId: $"r{i}")));
        }

        _factory.Time.Advance(TimeSpan.FromSeconds(60));
        var response = await _client.PostAsync(Url, TestData.Json(TestData.Request(reservationId: "later")));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>Invalid requests are rejected before the limiter, so they do not use up the supplier's quota.</summary>
    [Fact]
    public async Task Post_InvalidRequests_DoNotConsumeQuota()
    {
        for (var i = 0; i < 5; i++)
        {
            await _client.PostAsync(Url, TestData.Json(TestData.Request(price: -1m)));
        }

        var response = await _client.PostAsync(Url, TestData.Json(TestData.Request()));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // ---------- 500 ----------

    /// <summary>An unexpected failure returns a 500 problem response and logs at Error.</summary>
    [Fact]
    public async Task Post_RepositoryFails_Returns500_AndLogsError()
    {
        var failing = new Mock<IReservationRepository>();
        failing.Setup(r => r.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));
        using var factory = new MizeApiFactory { RepositoryOverride = failing.Object };
        using var client = factory.CreateClient();

        var response = await client.PostAsync(Url, TestData.Json(TestData.Request()));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains(factory.Logs.Entries, e => e.Level == LogLevel.Error);
    }
}
