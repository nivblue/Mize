using Microsoft.Extensions.Options;
using Mize.Api.RateLimiting;
using Mize.Tests.Helpers;

namespace Mize.Tests.Unit;

/// <summary>Unit tests for <see cref="SlidingWindowRateLimiter"/> using a fake clock.</summary>
public class SlidingWindowRateLimiterTests
{
    private readonly FakeTimeProvider _time = new();

    /// <summary>Builds a limiter with the given limit and a 60-second window.</summary>
    private SlidingWindowRateLimiter Create(int max = 100, int windowSeconds = 60) =>
        new(Options.Create(new RateLimitOptions { MaxRequests = max, WindowSeconds = windowSeconds }), _time);

    /// <summary>Exactly 100 requests are allowed and the 101st is rejected.</summary>
    [Fact]
    public void TryAcquire_AllowsUpToLimit_ThenRejects()
    {
        var limiter = Create();

        for (var i = 0; i < 100; i++)
        {
            Assert.True(limiter.TryAcquire("s").Allowed);
        }

        var rejected = limiter.TryAcquire("s");
        Assert.False(rejected.Allowed);
        Assert.Equal(TimeSpan.FromSeconds(60), rejected.RetryAfter);
    }

    /// <summary>Different suppliers have independent windows.</summary>
    [Fact]
    public void TryAcquire_IsPerSupplier()
    {
        var limiter = Create(max: 1);

        Assert.True(limiter.TryAcquire("a").Allowed);
        Assert.False(limiter.TryAcquire("a").Allowed);
        Assert.True(limiter.TryAcquire("b").Allowed);
    }

    /// <summary>Once the oldest request is a full window old, a slot frees up (boundary is exclusive).</summary>
    [Fact]
    public void TryAcquire_AfterWindowClears_AllowsAgain()
    {
        var limiter = Create(max: 2);
        limiter.TryAcquire("s");
        limiter.TryAcquire("s");

        _time.Advance(TimeSpan.FromSeconds(59));
        Assert.False(limiter.TryAcquire("s").Allowed);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(limiter.TryAcquire("s").Allowed);
    }

    /// <summary>The window is truly rolling: slots free up one by one as old hits expire, not all at once.</summary>
    [Fact]
    public void TryAcquire_IsRolling_NotFixedBuckets()
    {
        var limiter = Create(max: 2);
        limiter.TryAcquire("s");                       // t=0
        _time.Advance(TimeSpan.FromSeconds(30));
        limiter.TryAcquire("s");                       // t=30

        _time.Advance(TimeSpan.FromSeconds(30));       // t=60: first expired, second still counts
        Assert.True(limiter.TryAcquire("s").Allowed);  // t=60 (now hits: 30, 60)
        Assert.False(limiter.TryAcquire("s").Allowed);
    }

    /// <summary>Rejected requests do not count, so the retry delay is measured from the oldest accepted request.</summary>
    [Fact]
    public void TryAcquire_RejectedRequestsDoNotExtendTheBlock()
    {
        var limiter = Create(max: 1);
        limiter.TryAcquire("s");                       // t=0

        _time.Advance(TimeSpan.FromSeconds(10));
        var rejected = limiter.TryAcquire("s");        // rejected, must not be recorded

        Assert.False(rejected.Allowed);
        Assert.Equal(TimeSpan.FromSeconds(50), rejected.RetryAfter);

        _time.Advance(TimeSpan.FromSeconds(50));       // t=60: first hit expired
        Assert.True(limiter.TryAcquire("s").Allowed);
    }

    /// <summary>Concurrent callers can never exceed the limit.</summary>
    [Fact]
    public void TryAcquire_UnderConcurrency_NeverExceedsLimit()
    {
        var limiter = Create(max: 50);

        var allowed = 0;
        Parallel.For(0, 500, _ =>
        {
            if (limiter.TryAcquire("s").Allowed)
            {
                Interlocked.Increment(ref allowed);
            }
        });

        Assert.Equal(50, allowed);
    }

    /// <summary>Non-positive options are rejected at construction.</summary>
    [Theory]
    [InlineData(0, 60)]
    [InlineData(-1, 60)]
    [InlineData(100, 0)]
    public void Constructor_InvalidOptions_Throws(int max, int windowSeconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(max, windowSeconds));
    }
}
