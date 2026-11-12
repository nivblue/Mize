using Microsoft.Extensions.Options;

namespace Mize.Api.Stats;

/// <summary>
/// Background service that saves the cached statistics to the database every <see cref="StatsOptions.FlushIntervalSeconds"/>
/// seconds, and once more when the application stops gracefully (after the web server has finished its requests, because
/// hosted services stop in reverse order of registration and the web server is registered last).
/// A failing flush is logged and retried on the next tick; nothing is lost while the process lives.
/// </summary>
public sealed class StatsFlushService : BackgroundService
{
    private readonly IStatsFlusher _flusher;
    private readonly TimeSpan _interval;
    private readonly ILogger<StatsFlushService> _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="flusher">What to flush.</param>
    /// <param name="options">The flush interval; must be positive.</param>
    /// <param name="logger">Logger.</param>
    public StatsFlushService(IStatsFlusher flusher, IOptions<StatsOptions> options, ILogger<StatsFlushService> logger)
    {
        if (options.Value.FlushIntervalSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Stats:FlushIntervalSeconds must be positive.");
        }

        _flusher = flusher;
        _interval = TimeSpan.FromSeconds(options.Value.FlushIntervalSeconds);
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            // The only way out is cancellation: WaitForNextTickAsync returns false only if the timer is disposed, and this
            // method owns the timer and disposes it when it ends, so the result never needs checking.
            while (true)
            {
                await timer.WaitForNextTickAsync(stoppingToken);
                await FlushSafelyAsync(isFinal: false, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown: the loop ends, StopAsync performs the final flush.
        }
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await FlushSafelyAsync(isFinal: true, cancellationToken);
    }

    /// <summary>
    /// Flushes, logging (never throwing) any failure, so one bad flush cannot stop the loop or the shutdown. A periodic
    /// flush cancelled by the shutdown itself is ignored, because the final flush follows and covers it.
    /// </summary>
    private async Task FlushSafelyAsync(bool isFinal, CancellationToken cancellationToken)
    {
        try
        {
            await _flusher.FlushAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (!isFinal)
        {
            // Interrupted by the shutdown; the final flush will save everything.
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                isFinal
                    ? "The final flush of the statistics failed; increments not yet saved are lost"
                    : "Flushing the statistics failed; the counters are kept and the flush will be retried");
        }
    }
}
