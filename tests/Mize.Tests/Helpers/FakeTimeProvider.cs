namespace Mize.Tests.Helpers;

/// <summary>A manually advanced clock so rolling-window behaviour is tested deterministically without sleeping.</summary>
public sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    /// <summary>Creates the clock at a fixed start instant (default 2026-07-01T10:00:00Z).</summary>
    public FakeTimeProvider(DateTimeOffset? start = null)
    {
        _now = start ?? new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero);
    }

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => _now;

    /// <summary>Moves the clock forward.</summary>
    /// <param name="amount">How far to advance.</param>
    public void Advance(TimeSpan amount) => _now += amount;
}
