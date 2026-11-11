namespace Mize.Api.RateLimiting;

/// <summary>Result of asking the rate limiter for permission.</summary>
/// <param name="Allowed">True when the request may proceed (and has been counted).</param>
/// <param name="RetryAfter">When rejected: how long until the window frees a slot. <see cref="TimeSpan.Zero"/> when allowed.</param>
public sealed record RateLimitDecision(bool Allowed, TimeSpan RetryAfter);

/// <summary>Decides whether a supplier may send another request.</summary>
public interface IRateLimiter
{
    /// <summary>
    /// Atomically checks the supplier's window and, if there is room, records the request.
    /// Rejected requests are not recorded.
    /// </summary>
    /// <param name="supplierId">The supplier making the request.</param>
    /// <returns>The decision.</returns>
    RateLimitDecision TryAcquire(string supplierId);
}
