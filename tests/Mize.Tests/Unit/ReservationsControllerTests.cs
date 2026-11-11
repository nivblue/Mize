using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Mize.Api.Controllers;
using Mize.Api.Models;
using Mize.Api.RateLimiting;
using Mize.Api.Services;
using Mize.Api.Stats;
using Mize.Api.Validation;
using Mize.Tests.Helpers;

namespace Mize.Tests.Unit;

/// <summary>Unit tests for <see cref="ReservationsController"/>, with every collaborator except the validator mocked.</summary>
public class ReservationsControllerTests
{
    private readonly Mock<IRateLimiter> _limiter = new();
    private readonly Mock<IReservationService> _service = new();
    private readonly Mock<ISupplierStatsTracker> _stats = new();
    private readonly ListLogger<ReservationsController> _logger = new();
    private readonly ReservationsController _controller;

    /// <summary>Creates the controller with an HttpContext (needed for response headers) and an allowing limiter.</summary>
    public ReservationsControllerTests()
    {
        _limiter.Setup(l => l.TryAcquire(It.IsAny<string>())).Returns(new RateLimitDecision(true, TimeSpan.Zero));
        _controller = new ReservationsController(new ReservationRequestValidator(), _limiter.Object, _service.Object, _stats.Object, _logger)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    /// <summary>Makes the mocked service return <paramref name="status"/>.</summary>
    private void ServiceReturns(IngestStatus status) =>
        _service.Setup(s => s.IngestAsync(It.IsAny<ReservationRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(status);

    /// <summary>A created reservation yields HTTP 201 with status "Created" and is counted.</summary>
    [Fact]
    public async Task Ingest_Created_Returns201_AndRecordsStats()
    {
        ServiceReturns(IngestStatus.Created);

        var result = await _controller.Ingest(TestData.Request(), CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(201, obj.StatusCode);
        Assert.Equal(IngestStatus.Created, Assert.IsType<IngestResponse>(obj.Value).Status);
        _stats.Verify(s => s.RecordOutcome("supplier-1", IngestStatus.Created), Times.Once);
    }

    /// <summary>Updated and unchanged outcomes both yield HTTP 200 with the matching status and a message.</summary>
    [Theory]
    [InlineData(IngestStatus.Updated)]
    [InlineData(IngestStatus.Unchanged)]
    public async Task Ingest_NonCreated_Returns200WithStatus(IngestStatus status)
    {
        ServiceReturns(status);

        var result = await _controller.Ingest(TestData.Request(), CancellationToken.None);

        var body = Assert.IsType<IngestResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(status, body.Status);
        Assert.False(string.IsNullOrWhiteSpace(body.Message));
        _stats.Verify(s => s.RecordOutcome("supplier-1", status), Times.Once);
    }

    /// <summary>The "unchanged" message states clearly that nothing changed.</summary>
    [Fact]
    public async Task Ingest_Unchanged_MessageSaysNothingChanged()
    {
        ServiceReturns(IngestStatus.Unchanged);

        var result = await _controller.Ingest(TestData.Request(), CancellationToken.None);

        var body = Assert.IsType<IngestResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Contains("nothing changed", body.Message);
    }

    /// <summary>An invalid request gets HTTP 400, is logged at Warning, and never reaches the limiter, service or stats.</summary>
    [Fact]
    public async Task Ingest_Invalid_Returns400_LogsWarning_AndSkipsEverythingElse()
    {
        var result = await _controller.Ingest(TestData.Request(price: -1m), CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.IsType<ValidationProblemDetails>(bad.Value);
        Assert.Equal(LogLevel.Warning, Assert.Single(_logger.Entries).Level);
        _limiter.Verify(l => l.TryAcquire(It.IsAny<string>()), Times.Never);
        _service.Verify(s => s.IngestAsync(It.IsAny<ReservationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _stats.VerifyNoOtherCalls();
    }

    /// <summary>A throttled request gets HTTP 429 with Retry-After, is logged at Warning, counted as throttled and not ingested.</summary>
    [Fact]
    public async Task Ingest_Throttled_Returns429_WithRetryAfter_LogsWarning_AndCountsThrottle()
    {
        _limiter.Setup(l => l.TryAcquire("supplier-1")).Returns(new RateLimitDecision(false, TimeSpan.FromSeconds(12.2)));

        var result = await _controller.Ingest(TestData.Request(), CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(429, obj.StatusCode);
        Assert.Equal("13", _controller.Response.Headers["Retry-After"].ToString());
        Assert.Equal(LogLevel.Warning, Assert.Single(_logger.Entries).Level);
        _stats.Verify(s => s.RecordThrottled("supplier-1"), Times.Once);
        _stats.Verify(s => s.RecordOutcome(It.IsAny<string>(), It.IsAny<IngestStatus>()), Times.Never);
        _service.Verify(s => s.IngestAsync(It.IsAny<ReservationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>Exceptions from the service are not swallowed and nothing is counted as ingested.</summary>
    [Fact]
    public async Task Ingest_ServiceThrows_Propagates_AndDoesNotRecordOutcome()
    {
        _service.Setup(s => s.IngestAsync(It.IsAny<ReservationRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _controller.Ingest(TestData.Request(), CancellationToken.None));

        _stats.Verify(s => s.RecordOutcome(It.IsAny<string>(), It.IsAny<IngestStatus>()), Times.Never);
    }

    /// <summary>The stats endpoint returns whatever the tracker reports for the supplier.</summary>
    [Fact]
    public void GetStats_ReturnsTrackerSnapshot()
    {
        var snapshot = new SupplierStats("s", 5, 2, new IngestBreakdown(3, 1, 1));
        _stats.Setup(s => s.Get("s")).Returns(snapshot);

        var result = _controller.GetStats("s");

        Assert.Same(snapshot, Assert.IsType<OkObjectResult>(result.Result).Value);
    }
}
