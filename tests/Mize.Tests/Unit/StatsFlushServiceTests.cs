using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mize.Api.Stats;
using Mize.Tests.Helpers;

namespace Mize.Tests.Unit;

/// <summary>Unit tests for <see cref="StatsFlushService"/> with a fake flusher and short intervals.</summary>
public class StatsFlushServiceTests
{
    private readonly RecordingFlusher _flusher = new();
    private readonly ListLogger<StatsFlushService> _logger = new();

    /// <summary>A flusher that counts its calls and can be told to fail or to block.</summary>
    private sealed class RecordingFlusher : IStatsFlusher
    {
        private int _calls;
        private int _completed;

        /// <summary>Number of times the service started a flush.</summary>
        public int Calls => Volatile.Read(ref _calls);

        /// <summary>Number of flushes that finished successfully.</summary>
        public int Completed => Volatile.Read(ref _completed);

        /// <summary>How many of the next calls throw.</summary>
        public int FailTimes { get; set; }

        /// <summary>When true, the first call blocks until it is cancelled.</summary>
        public bool BlockFirstCall { get; set; }

        /// <inheritdoc />
        public async Task FlushAsync(CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);
            if (BlockFirstCall && call == 1)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            if (call <= FailTimes)
            {
                throw new InvalidOperationException("db down");
            }

            Interlocked.Increment(ref _completed);
        }
    }

    /// <summary>Creates the service with the given interval in seconds.</summary>
    private StatsFlushService Create(double seconds) =>
        new(_flusher, Options.Create(new StatsOptions { FlushIntervalSeconds = seconds }), _logger);

    /// <summary>Polls until <paramref name="condition"/> holds, failing after five seconds.</summary>
    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "the condition was not reached within 5 seconds");
            await Task.Delay(20);
        }
    }

    /// <summary>The service flushes again and again at the configured interval.</summary>
    [Fact]
    public async Task Runs_FlushesPeriodically()
    {
        var service = Create(0.05);

        await service.StartAsync(CancellationToken.None);
        await Eventually(() => _flusher.Completed >= 3);
        await service.StopAsync(CancellationToken.None);
    }

    /// <summary>On a graceful stop one last flush happens, even if no interval has elapsed.</summary>
    [Fact]
    public async Task Stop_PerformsAFinalFlush()
    {
        var service = Create(3600);

        await service.StartAsync(CancellationToken.None);
        Assert.Equal(0, _flusher.Calls);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(1, _flusher.Completed);
    }

    /// <summary>A failing flush is logged as an error and the loop keeps going.</summary>
    [Fact]
    public async Task FailingFlush_IsLogged_AndTheLoopContinues()
    {
        _flusher.FailTimes = 1;
        var service = Create(0.05);

        await service.StartAsync(CancellationToken.None);
        await Eventually(() => _flusher.Completed >= 1);
        await service.StopAsync(CancellationToken.None);

        var error = Assert.Single(_logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("retried", error.Message);
    }

    /// <summary>A failing final flush is logged and does not make the shutdown throw.</summary>
    [Fact]
    public async Task FailingFinalFlush_IsLogged_AndStopDoesNotThrow()
    {
        _flusher.FailTimes = int.MaxValue;
        var service = Create(3600);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        var error = Assert.Single(_logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("final flush", error.Message);
    }

    /// <summary>A periodic flush interrupted by the shutdown itself is not an error; the final flush covers it.</summary>
    [Fact]
    public async Task PeriodicFlush_InterruptedByShutdown_IsNotAnError()
    {
        _flusher.BlockFirstCall = true;
        var service = Create(0.05);

        await service.StartAsync(CancellationToken.None);
        await Eventually(() => _flusher.Calls >= 1);
        await service.StopAsync(CancellationToken.None);

        Assert.DoesNotContain(_logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Equal(1, _flusher.Completed);   // only the final flush completed
    }

    /// <summary>A non-positive interval is rejected at construction.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_NonPositiveInterval_Throws(double seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(seconds));
    }
}
