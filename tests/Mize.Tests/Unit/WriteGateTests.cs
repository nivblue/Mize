using Mize.Api.Services;

namespace Mize.Tests.Unit;

/// <summary>Unit tests for <see cref="WriteGate"/>.</summary>
public class WriteGateTests
{
    private readonly WriteGate _gate = new();

    /// <summary>A second caller waits until the first one releases the gate.</summary>
    [Fact]
    public async Task Enter_WhileHeld_Waits_UntilReleased()
    {
        var first = await _gate.EnterAsync(CancellationToken.None);

        var second = _gate.EnterAsync(CancellationToken.None);
        await Task.Delay(100);
        Assert.False(second.IsCompleted);

        first.Dispose();
        using var handle = await second.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>Disposing a handle twice releases only once, so the gate can never be entered by two callers.</summary>
    [Fact]
    public async Task Dispose_Twice_ReleasesOnlyOnce()
    {
        var first = await _gate.EnterAsync(CancellationToken.None);
        first.Dispose();
        first.Dispose();

        using var second = await _gate.EnterAsync(CancellationToken.None);
        var third = _gate.EnterAsync(CancellationToken.None);
        await Task.Delay(100);

        Assert.False(third.IsCompleted);
    }

    /// <summary>A waiting caller can be cancelled.</summary>
    [Fact]
    public async Task Enter_Cancelled_WhileWaiting_Throws()
    {
        using var held = await _gate.EnterAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _gate.EnterAsync(cts.Token));
    }

    /// <summary>Under heavy contention only one caller is ever inside.</summary>
    [Fact]
    public async Task Enter_UnderContention_NeverLetsTwoCallersIn()
    {
        var inside = 0;
        var maxInside = 0;

        await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(async () =>
        {
            using var handle = await _gate.EnterAsync(CancellationToken.None);
            var now = Interlocked.Increment(ref inside);
            InterlockedMax(ref maxInside, now);
            await Task.Delay(1);
            Interlocked.Decrement(ref inside);
        })));

        Assert.Equal(1, maxInside);
    }

    /// <summary>Atomically raises <paramref name="target"/> to <paramref name="value"/> when it is lower.</summary>
    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
