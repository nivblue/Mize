using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Mize.E2ETests;

/// <summary>
/// E2E tests of both endpoints against the real, running API container over real HTTP, verifying persistence by
/// reading the SQLite file the container writes to. Every test uses its own supplier id, so tests are independent
/// and can run in parallel without sharing rate-limit windows or statistics.
/// </summary>
public class IngestFlowE2ETests : IClassFixture<E2EFixture>
{
    private const string IngestUrl = "/api/reservations/ingest";
    private readonly E2EFixture _api;

    /// <summary>Receives the shared fixture.</summary>
    public IngestFlowE2ETests(E2EFixture api)
    {
        _api = api;
    }

    /// <summary>Reads a response body as a JSON document.</summary>
    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    /// <summary>Reads the status field of an ingest response.</summary>
    private static async Task<string?> StatusOf(HttpResponseMessage response) =>
        (await JsonOf(response)).GetProperty("status").GetString();

    /// <summary>A new reservation returns 201 and really lands in the SQLite file on the mounted volume.</summary>
    [Fact]
    public async Task Ingest_New_Returns201_AndIsWrittenToTheSqliteFile()
    {
        var supplier = E2EFixture.NewSupplierId();

        var response = await _api.Client.PostAsync(IngestUrl, Payload.Reservation(supplier, "r1", price: 450.00m));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("created", await StatusOf(response));
        var row = await _api.ReadRowAsync(supplier, "r1");
        Assert.NotNull(row);
        Assert.Equal("room-42", row!.RoomId);
        Assert.Equal(450.00m, row.Price);
    }

    /// <summary>Sending the same reservation again returns 200 "unchanged" with a clear message; the row is untouched.</summary>
    [Fact]
    public async Task Ingest_ExactDuplicate_Returns200Unchanged()
    {
        var supplier = E2EFixture.NewSupplierId();
        await _api.Client.PostAsync(IngestUrl, Payload.Reservation(supplier, "r1"));

        var response = await _api.Client.PostAsync(IngestUrl, Payload.Reservation(supplier, "r1"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonOf(response);
        Assert.Equal("unchanged", body.GetProperty("status").GetString());
        Assert.Contains("nothing changed", body.GetProperty("message").GetString());
    }

    /// <summary>Same id with different details returns 200 "updated" and the file shows the new details.</summary>
    [Fact]
    public async Task Ingest_ChangedDetails_Returns200Updated_AndUpdatesTheRow()
    {
        var supplier = E2EFixture.NewSupplierId();
        await _api.Client.PostAsync(IngestUrl, Payload.Reservation(supplier, "r1", price: 450m));

        var response = await _api.Client.PostAsync(IngestUrl,
            Payload.Reservation(supplier, "r1", price: 600.50m, roomId: "room-7", updatedAtUtc: "2026-07-02T09:00:00Z"));

        Assert.Equal("updated", await StatusOf(response));
        var row = await _api.ReadRowAsync(supplier, "r1");
        Assert.Equal(600.50m, row!.Price);
        Assert.Equal("room-7", row.RoomId);
    }

    /// <summary>An out-of-order (older) update returns 200 "stale" and leaves the stored row alone.</summary>
    [Fact]
    public async Task Ingest_OlderUpdate_Returns200Stale_AndKeepsTheRow()
    {
        var supplier = E2EFixture.NewSupplierId();
        await _api.Client.PostAsync(IngestUrl, Payload.Reservation(supplier, "r1", price: 450m));

        var response = await _api.Client.PostAsync(IngestUrl,
            Payload.Reservation(supplier, "r1", price: 1m, updatedAtUtc: "2026-06-01T00:00:00Z"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("stale", await StatusOf(response));
        Assert.Equal(450m, (await _api.ReadRowAsync(supplier, "r1"))!.Price);
    }

    /// <summary>Two suppliers may use the same reservationId without affecting each other.</summary>
    [Fact]
    public async Task Ingest_SameReservationIdForTwoSuppliers_AreIndependent()
    {
        var a = E2EFixture.NewSupplierId();
        var b = E2EFixture.NewSupplierId();

        await _api.Client.PostAsync(IngestUrl, Payload.Reservation(a, "shared", price: 1m));
        var second = await _api.Client.PostAsync(IngestUrl, Payload.Reservation(b, "shared", price: 2m));

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(1m, (await _api.ReadRowAsync(a, "shared"))!.Price);
        Assert.Equal(2m, (await _api.ReadRowAsync(b, "shared"))!.Price);
    }

    /// <summary>20 simultaneous identical requests produce exactly one 201 and 19 "unchanged", with no server errors.</summary>
    [Fact]
    public async Task Ingest_ParallelDuplicates_CreateExactlyOne()
    {
        var supplier = E2EFixture.NewSupplierId();

        var responses = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => _api.Client.PostAsync(IngestUrl, Payload.Reservation(supplier, "r1"))));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(19, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
    }

    /// <summary>Business-rule violations return 400 with the offending field and nothing is written.</summary>
    [Fact]
    public async Task Ingest_InvalidPrice_Returns400_AndNothingIsWritten()
    {
        var supplier = E2EFixture.NewSupplierId();

        var response = await _api.Client.PostAsync(IngestUrl, Payload.Reservation(supplier, "r1", price: -5m));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await JsonOf(response)).GetProperty("errors").TryGetProperty("price", out _));
        Assert.Null(await _api.ReadRowAsync(supplier, "r1"));
    }

    /// <summary>A non-UTC timestamp is rejected with 400.</summary>
    [Fact]
    public async Task Ingest_NonUtcTimestamp_Returns400()
    {
        var supplier = E2EFixture.NewSupplierId();

        var response = await _api.Client.PostAsync(IngestUrl,
            Payload.Reservation(supplier, "r1", checkIn: "2026-08-01T00:00:00+02:00"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>Malformed JSON and a missing required property both return 400.</summary>
    [Fact]
    public async Task Ingest_MalformedOrIncompleteJson_Returns400()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _api.Client.PostAsync(IngestUrl, Payload.Raw("{ nope"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _api.Client.PostAsync(IngestUrl, Payload.Raw("{\"supplierId\":\"x\"}"))).StatusCode);
    }

    /// <summary>A non-JSON content type returns 415.</summary>
    [Fact]
    public async Task Ingest_TextPlain_Returns415()
    {
        var response = await _api.Client.PostAsync(IngestUrl, new StringContent("hi", System.Text.Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    /// <summary>A supplier that never sent anything has zeroed stats (200, not 404).</summary>
    [Fact]
    public async Task Stats_UnknownSupplier_ReturnsZeros()
    {
        var stats = await _api.Client.GetFromJsonAsync<JsonElement>($"/api/reservations/stats/{E2EFixture.NewSupplierId()}");

        Assert.Equal(0, stats.GetProperty("ingested").GetInt32());
        Assert.Equal(0, stats.GetProperty("throttled").GetInt32());
    }

    /// <summary>Stats count created, unchanged, updated and stale outcomes; invalid requests are not counted.</summary>
    [Fact]
    public async Task Stats_ReflectEveryOutcome()
    {
        var supplier = E2EFixture.NewSupplierId();
        await _api.Client.PostAsync(IngestUrl, Payload.Reservation(supplier, "r1", price: 1m));                                          // created
        await _api.Client.PostAsync(IngestUrl, Payload.Reservation(supplier, "r1", price: 1m));                                          // unchanged
        await _api.Client.PostAsync(IngestUrl, Payload.Reservation(supplier, "r1", price: 2m, updatedAtUtc: "2026-07-02T00:00:00Z"));    // updated
        await _api.Client.PostAsync(IngestUrl, Payload.Reservation(supplier, "r1", price: 3m, updatedAtUtc: "2026-06-01T00:00:00Z"));    // stale
        await _api.Client.PostAsync(IngestUrl, Payload.Reservation(supplier, "r2", price: -1m));                                         // invalid

        var stats = await _api.Client.GetFromJsonAsync<JsonElement>($"/api/reservations/stats/{supplier}");

        Assert.Equal(3, stats.GetProperty("ingested").GetInt32());
        Assert.Equal(0, stats.GetProperty("throttled").GetInt32());
        var breakdown = stats.GetProperty("breakdown");
        Assert.Equal(1, breakdown.GetProperty("created").GetInt32());
        Assert.Equal(1, breakdown.GetProperty("unchanged").GetInt32());
        Assert.Equal(1, breakdown.GetProperty("updated").GetInt32());
        Assert.Equal(1, breakdown.GetProperty("stale").GetInt32());
    }
}
