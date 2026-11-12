using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using System.Globalization;
using Mize.Api.Infrastructure;
using Mize.Api.Models;
using Mize.Api.RateLimiting;
using Mize.Api.Services;
using Mize.Api.Stats;
using Mize.Api.Validation;

namespace Mize.Api.Controllers;

/// <summary>
/// Reservation endpoints. The ingest pipeline is: validate (400, Warning) → rate-limit (429, Warning)
/// → ingest. Invalid requests are rejected before the limiter, so they never consume quota.
/// </summary>
[ApiController]
[Route("api/reservations")]
public sealed class ReservationsController : ControllerBase
{
    private readonly ReservationRequestValidator _validator;
    private readonly IRateLimiter _rateLimiter;
    private readonly IReservationService _service;
    private readonly ISupplierStatsTracker _stats;
    private readonly ILogger<ReservationsController> _logger;

    /// <summary>Creates the controller.</summary>
    /// <param name="validator">Business validation.</param>
    /// <param name="rateLimiter">Per-supplier rate limiter.</param>
    /// <param name="service">Ingest business logic.</param>
    /// <param name="stats">Per-supplier counters.</param>
    /// <param name="logger">Logger.</param>
    public ReservationsController(
        ReservationRequestValidator validator,
        IRateLimiter rateLimiter,
        IReservationService service,
        ISupplierStatsTracker stats,
        ILogger<ReservationsController> logger)
    {
        _validator = validator;
        _rateLimiter = rateLimiter;
        _service = service;
        _stats = stats;
        _logger = logger;
    }

    /// <summary>
    /// <c>POST /api/reservations/ingest</c>. Returns 201 (created), 200 (updated / unchanged, with a
    /// <c>status</c> field), 400 (invalid payload) or 429 (rate limit exceeded, with <c>Retry-After</c>).
    /// </summary>
    /// <param name="request">The reservation payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The HTTP result.</returns>
    [HttpPost("ingest")]
    [ProducesResponseType(typeof(IngestResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(IngestResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Ingest([FromBody] ReservationRequest request, CancellationToken cancellationToken)
    {
        var errors = _validator.Validate(request);
        if (errors.Count > 0)
        {
            _logger.LogWarning(
                "Rejected reservation {ReservationId} from supplier {SupplierId}: invalid fields {InvalidFields}",
                request.ReservationId, request.SupplierId, string.Join(", ", errors.Keys));
            return ProblemResults.ValidationFailed(errors.ToDictionary(e => e.Key, e => e.Value));
        }

        var decision = _rateLimiter.TryAcquire(request.SupplierId);
        if (!decision.Allowed)
        {
            _stats.RecordThrottled(request.SupplierId);
            var retryAfterSeconds = ProblemResults.RetryAfterSeconds(decision.RetryAfter);
            _logger.LogWarning(
                "Throttled supplier {SupplierId}: rate limit exceeded, retry in {RetryAfterSeconds}s",
                request.SupplierId, retryAfterSeconds);
            Response.Headers[HeaderNames.RetryAfter] = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            return ProblemResults.TooManyRequests(decision.RetryAfter);
        }

        // The service records the outcome in the statistics itself (inside its write gate, right after the commit).
        var status = await _service.IngestAsync(request, cancellationToken);

        var response = new IngestResponse(status, request.SupplierId, request.ReservationId, DescribeOutcome(status));
        return status == IngestStatus.Created
            ? StatusCode(StatusCodes.Status201Created, response)
            : Ok(response);
    }

    /// <summary>
    /// <c>GET /api/reservations/stats/{supplierId}</c>. Returns how many reservations were ingested (stored; duplicates and
    /// updates do not add to it) and how many requests were throttled for the supplier, plus the per-outcome counters
    /// (zeros if the supplier has not been seen). Served from the in-memory cache after one database read per supplier.
    /// </summary>
    /// <param name="supplierId">The supplier to report on (case-sensitive).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>HTTP 200 with <see cref="SupplierStats"/>.</returns>
    [HttpGet("stats/{supplierId}")]
    [ProducesResponseType(typeof(SupplierStats), StatusCodes.Status200OK)]
    public async Task<ActionResult<SupplierStats>> GetStats(string supplierId, CancellationToken cancellationToken) =>
        Ok(await _stats.GetAsync(supplierId, cancellationToken));

    /// <summary>Maps an outcome to the human-readable message returned to the supplier.</summary>
    private static string DescribeOutcome(IngestStatus status) => status switch
    {
        IngestStatus.Created => "Reservation created.",
        IngestStatus.Updated => "Reservation updated with the new details.",
        IngestStatus.Unchanged => "Reservation already exists with identical details; nothing changed.",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown ingest status.")
    };
}
