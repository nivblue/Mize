namespace Mize.Api.RateLimiting;

/// <summary>Configuration for the per-supplier rolling-window rate limiter (section <c>RateLimit</c>).</summary>
public sealed class RateLimitOptions
{
    /// <summary>Name of the configuration section these options bind to.</summary>
    public const string SectionName = "RateLimit";

    /// <summary>Maximum accepted requests per supplier within <see cref="WindowSeconds"/>. Default 100.</summary>
    public int MaxRequests { get; set; } = 100;

    /// <summary>Length of the rolling window in seconds. Default 60.</summary>
    public int WindowSeconds { get; set; } = 60;
}
