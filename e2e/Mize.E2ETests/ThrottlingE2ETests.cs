using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Mize.E2ETests;

/// <summary>
/// E2E test of the real rate limit (100 requests per rolling 60 seconds) with no fake clock. It really waits for the
/// window to clear, so this single test takes roughly a minute.
/// </summary>
public class ThrottlingE2ETests : IClassFixture<E2EFixture>
{
    private const string IngestUrl = "/api/reservations/ingest";
    private readonly E2EFixture _api;

    /// <summary>Receives the shared fixture.</summary>
    public ThrottlingE2ETests(E2EFixture api)
    {
        _api = api;
    }

    /// <summary>
    /// 100 requests are accepted, the 101st gets 429 with Retry-After (and writes nothing), other suppliers are
    /// unaffected, the stats show 100 vs 1, and after the window clears the supplier is accepted again.
    /// </summary>
    [Fact]
    public async Task Throttling_101stRequestIsRejected_AndSupplierRecoversAfterTheWindow()
    {
        var noisy = E2EFixture.NewSupplierId();
        var other = E2EFixture.NewSupplierId();

        for (var i = 0; i < 100; i++)
        {
            var ok = await _api.Client.PostAsync(IngestUrl, Payload.Reservation(noisy, $"r{i}"));
            Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        }

        // 101st request within the window: rejected, with a usable Retry-After, and not persisted.
        var rejected = await _api.Client.PostAsync(IngestUrl, Payload.Reservation(noisy, "r100"));
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        var retryAfter = int.Parse(rejected.Headers.GetValues("Retry-After").Single());
        Assert.InRange(retryAfter, 1, 60);
        Assert.Null(await _api.ReadRowAsync(noisy, "r100"));

        // Another supplier is not affected.
        var neighbour = await _api.Client.PostAsync(IngestUrl, Payload.Reservation(other, "r1"));
        Assert.Equal(HttpStatusCode.Created, neighbour.StatusCode);

        // Stats: 100 ingested vs 1 throttled.
        var stats = await _api.Client.GetFromJsonAsync<JsonElement>($"/api/reservations/stats/{noisy}");
        Assert.Equal(100, stats.GetProperty("ingested").GetInt32());
        Assert.Equal(1, stats.GetProperty("throttled").GetInt32());

        // Wait (for real) until the window has cleared, then the supplier is accepted again.
        await Task.Delay(TimeSpan.FromSeconds(retryAfter + 1));
        var recovered = await _api.Client.PostAsync(IngestUrl, Payload.Reservation(noisy, "r101"));
        Assert.Equal(HttpStatusCode.Created, recovered.StatusCode);
    }
}
