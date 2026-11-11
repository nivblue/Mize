using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Mize.Api.RateLimiting;

/// <summary>
/// In-memory, exact rolling-window limiter: keeps one queue of accepted-request timestamps per supplier.
/// A request is allowed while fewer than <see cref="RateLimitOptions.MaxRequests"/> accepted requests
/// fall within the last <see cref="RateLimitOptions.WindowSeconds"/> seconds. State is per process and lost on restart.
/// </summary>
public sealed class SlidingWindowRateLimiter : IRateLimiter
{
    private readonly ConcurrentDictionary<string, SupplierWindow> _windows = new(StringComparer.Ordinal);
    private readonly int _maxRequests;
    private readonly TimeSpan _window;
    private readonly TimeProvider _time;

    /// <summary>Holds one supplier's accepted-request timestamps; <see cref="Gate"/> guards <see cref="Hits"/>.</summary>
    private sealed class SupplierWindow
    {
        /// <summary>Lock making check-and-record atomic for this supplier.</summary>
        public readonly object Gate = new();

        /// <summary>Timestamps of accepted requests, oldest first.</summary>
        public readonly Queue<DateTimeOffset> Hits = new();
    }

    /// <summary>Creates the limiter from configuration.</summary>
    /// <param name="options">Limit and window size; both must be positive.</param>
    /// <param name="time">Clock (replaceable in tests).</param>
    public SlidingWindowRateLimiter(IOptions<RateLimitOptions> options, TimeProvider time)
    {
        var value = options.Value;
        if (value.MaxRequests <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "RateLimit:MaxRequests must be positive.");
        }

        if (value.WindowSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "RateLimit:WindowSeconds must be positive.");
        }

        _maxRequests = value.MaxRequests;
        _window = TimeSpan.FromSeconds(value.WindowSeconds);
        _time = time;
    }

    /// <inheritdoc />
    public RateLimitDecision TryAcquire(string supplierId)
    {
        var now = _time.GetUtcNow();
        var window = _windows.GetOrAdd(supplierId, _ => new SupplierWindow());

        lock (window.Gate)
        {
            // Drop hits that have left the window (a hit exactly one window old is already expired).
            var cutoff = now - _window;
            while (window.Hits.Count > 0 && window.Hits.Peek() <= cutoff)
            {
                window.Hits.Dequeue();
            }

            if (window.Hits.Count >= _maxRequests)
            {
                var retryAfter = window.Hits.Peek() + _window - now;
                return new RateLimitDecision(false, retryAfter);
            }

            window.Hits.Enqueue(now);
            return new RateLimitDecision(true, TimeSpan.Zero);
        }
    }
}
