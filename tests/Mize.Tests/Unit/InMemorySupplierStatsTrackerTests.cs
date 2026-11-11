using Mize.Api.Models;
using Mize.Api.Stats;

namespace Mize.Tests.Unit;

/// <summary>Unit tests for <see cref="InMemorySupplierStatsTracker"/>.</summary>
public class InMemorySupplierStatsTrackerTests
{
    private readonly InMemorySupplierStatsTracker _tracker = new();

    /// <summary>A supplier that was never seen reports all zeros.</summary>
    [Fact]
    public void Get_UnknownSupplier_ReturnsZeros()
    {
        var stats = _tracker.Get("nobody");

        Assert.Equal("nobody", stats.SupplierId);
        Assert.Equal(0, stats.Ingested);
        Assert.Equal(0, stats.Throttled);
        Assert.Equal(new IngestBreakdown(0, 0, 0), stats.Breakdown);
    }

    /// <summary>Ingested is created + updated + unchanged, and the breakdown shows each.</summary>
    [Fact]
    public void RecordOutcome_CountsIngestedAsCreatedUpdatedUnchanged()
    {
        _tracker.RecordOutcome("s", IngestStatus.Created);
        _tracker.RecordOutcome("s", IngestStatus.Created);
        _tracker.RecordOutcome("s", IngestStatus.Updated);
        _tracker.RecordOutcome("s", IngestStatus.Unchanged);

        var stats = _tracker.Get("s");

        Assert.Equal(4, stats.Ingested);
        Assert.Equal(new IngestBreakdown(2, 1, 1), stats.Breakdown);
        Assert.Equal(0, stats.Throttled);
    }

    /// <summary>Throttled requests are counted separately from ingested ones.</summary>
    [Fact]
    public void RecordThrottled_IncrementsOnlyThrottled()
    {
        _tracker.RecordThrottled("s");
        _tracker.RecordThrottled("s");

        var stats = _tracker.Get("s");

        Assert.Equal(2, stats.Throttled);
        Assert.Equal(0, stats.Ingested);
    }

    /// <summary>Counters are isolated per supplier and the id is case-sensitive.</summary>
    [Fact]
    public void Counters_AreIsolatedPerSupplier()
    {
        _tracker.RecordOutcome("a", IngestStatus.Created);
        _tracker.RecordThrottled("b");

        Assert.Equal(1, _tracker.Get("a").Ingested);
        Assert.Equal(0, _tracker.Get("a").Throttled);
        Assert.Equal(1, _tracker.Get("b").Throttled);
        Assert.Equal(0, _tracker.Get("A").Ingested);
    }

    /// <summary>An out-of-range enum value is rejected rather than silently ignored.</summary>
    [Fact]
    public void RecordOutcome_UnknownStatus_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _tracker.RecordOutcome("s", (IngestStatus)99));
    }

    /// <summary>Concurrent updates never lose a count.</summary>
    [Fact]
    public void RecordOutcome_UnderConcurrency_DoesNotLoseCounts()
    {
        Parallel.For(0, 1000, _ => _tracker.RecordOutcome("s", IngestStatus.Created));

        Assert.Equal(1000, _tracker.Get("s").Ingested);
    }
}
