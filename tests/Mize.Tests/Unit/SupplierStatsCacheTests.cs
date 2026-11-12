using Mize.Api.Models;
using Mize.Api.Services;
using Mize.Api.Stats;
using Mize.Tests.Helpers;

namespace Mize.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="SupplierStatsCache"/> against a fake repository: when the database is read, how the counters
/// are maintained, and the strict rules that keep the cached numbers exact.
/// </summary>
public class SupplierStatsCacheTests
{
    private readonly FakeStatsRepository _repo = new();
    private readonly WriteGate _gate = new();
    private readonly SupplierStatsCache _cache;

    /// <summary>Creates the cache under test over a fake repository and a real write gate.</summary>
    public SupplierStatsCacheTests()
    {
        _cache = new SupplierStatsCache(_repo, _gate);
    }

    /// <summary>Gives a supplier existing data: a row count and saved counters.</summary>
    private void Existing(string supplier, long rows, StatsCounters saved)
    {
        _repo.RowsProvider = id => id == supplier ? rows : 0;
        _repo.Seed(supplier, saved);
    }

    // ---------- reading: when the database is touched ----------

    /// <summary>A supplier without data reports zeros and is NOT cached: nothing is remembered about it.</summary>
    [Fact]
    public async Task Get_UnknownSupplier_ReturnsZeros_AndIsNotCached()
    {
        var first = await _cache.GetAsync("nobody", CancellationToken.None);
        var second = await _cache.GetAsync("nobody", CancellationToken.None);

        Assert.Equal(new SupplierStats("nobody", 0, 0, new IngestBreakdown(0, 0, 0)), first);
        Assert.Equal(first, second);
        Assert.Equal(2, _repo.LoadCount);   // no entry was created, so every read asks the database again
    }

    /// <summary>The first read loads rows and counters from the database; later reads come from memory.</summary>
    [Fact]
    public async Task Get_ExistingSupplier_ReadsTheDatabaseOnce_ThenServesFromMemory()
    {
        Existing("a", rows: 5, new StatsCounters(5, 1, 2, 3));

        var stats = await _cache.GetAsync("a", CancellationToken.None);
        await _cache.GetAsync("a", CancellationToken.None);
        await _cache.GetAsync("a", CancellationToken.None);

        Assert.Equal(new SupplierStats("a", 5, 3, new IngestBreakdown(5, 1, 2)), stats);
        Assert.Equal(1, _repo.LoadCount);
    }

    /// <summary>A cached read does not need the write gate; only the first load does.</summary>
    [Fact]
    public async Task Get_FromMemory_DoesNotWaitForTheWriteGate()
    {
        Existing("a", 1, new StatsCounters(1, 0, 0, 0));
        await _cache.GetAsync("a", CancellationToken.None);

        using var held = await _gate.EnterAsync(CancellationToken.None);
        var stats = await _cache.GetAsync("a", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, stats.Ingested);
    }

    /// <summary>Rule 1: the first load waits for the write gate, so it cannot run while an ingest is mid-flight.</summary>
    [Fact]
    public async Task Get_OnFirstLoad_WaitsForTheWriteGate()
    {
        Existing("a", 1, new StatsCounters(1, 0, 0, 0));
        var held = await _gate.EnterAsync(CancellationToken.None);

        var pending = _cache.GetAsync("a", CancellationToken.None);
        await Task.Delay(150);
        Assert.False(pending.IsCompleted);

        held.Dispose();
        Assert.Equal(1, (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Ingested);
    }

    /// <summary>
    /// Rule 1 and 4 together, the race that could lose a row: an ingest is in flight (holds the gate, has committed row
    /// number 11, and records it) while the first read waits. The load must report 11, not 10 and not 12.
    /// </summary>
    [Fact]
    public async Task FirstLoad_RacingAnIngest_CountsTheNewRowExactlyOnce()
    {
        Existing("a", rows: 10, new StatsCounters(10, 0, 0, 0));
        var ingestHoldsTheGate = await _gate.EnterAsync(CancellationToken.None);
        var firstRead = _cache.GetAsync("a", CancellationToken.None);          // waits for the gate

        _repo.RowsProvider = _ => 11;                                          // the ingest commits its insert ...
        _cache.RecordOutcome("a", IngestStatus.Created);                       // ... and records it (cache not loaded yet)
        ingestHoldsTheGate.Dispose();

        var stats = await firstRead.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(11, stats.Ingested);
        Assert.Equal(11, stats.Breakdown.Created);   // saved 10 + this unflushed 1
    }

    // ---------- recording: memory only, strict rules ----------

    /// <summary>Rule 2: a created reservation increments ingested and the created counter, without any database read.</summary>
    [Fact]
    public async Task RecordOutcome_Created_AfterLoad_IncrementsIngested_WithoutReadingTheDatabase()
    {
        Existing("a", 5, new StatsCounters(5, 0, 0, 0));
        await _cache.GetAsync("a", CancellationToken.None);

        _cache.RecordOutcome("a", IngestStatus.Created);
        var stats = await _cache.GetAsync("a", CancellationToken.None);

        Assert.Equal(6, stats.Ingested);
        Assert.Equal(6, stats.Breakdown.Created);
        Assert.Equal(1, _repo.LoadCount);
    }

    /// <summary>Duplicates and updates never change the ingested (row) count, but are counted in the breakdown.</summary>
    [Fact]
    public async Task RecordOutcome_UpdatedAndUnchanged_DoNotChangeIngested()
    {
        Existing("a", 5, new StatsCounters(5, 0, 0, 0));
        await _cache.GetAsync("a", CancellationToken.None);

        _cache.RecordOutcome("a", IngestStatus.Updated);
        _cache.RecordOutcome("a", IngestStatus.Unchanged);
        _cache.RecordOutcome("a", IngestStatus.Unchanged);
        var stats = await _cache.GetAsync("a", CancellationToken.None);

        Assert.Equal(5, stats.Ingested);
        Assert.Equal(new IngestBreakdown(5, 1, 2), stats.Breakdown);
    }

    /// <summary>Rule 4: a created outcome for a supplier that is not loaded does not touch the row count (the load includes it).</summary>
    [Fact]
    public async Task RecordOutcome_Created_BeforeLoad_IsNotAddedToTheRowCount()
    {
        _repo.RowsProvider = _ => 6;                      // the database already contains that row
        _cache.RecordOutcome("a", IngestStatus.Created);

        var stats = await _cache.GetAsync("a", CancellationToken.None);

        Assert.Equal(6, stats.Ingested);                  // not 7
        Assert.Equal(1, stats.Breakdown.Created);         // the request counter still counts it (unflushed)
    }

    /// <summary>Throttled requests are added on top of the saved total.</summary>
    [Fact]
    public async Task RecordThrottled_AddsToTheSavedTotal()
    {
        Existing("a", 2, new StatsCounters(2, 0, 0, 10));
        _cache.RecordThrottled("a");
        _cache.RecordThrottled("a");
        _cache.RecordThrottled("a");

        Assert.Equal(13, (await _cache.GetAsync("a", CancellationToken.None)).Throttled);
    }

    /// <summary>Suppliers are independent.</summary>
    [Fact]
    public async Task Counters_AreIsolatedPerSupplier()
    {
        _repo.RowsProvider = _ => 0;
        _cache.RecordOutcome("a", IngestStatus.Created);
        _cache.RecordThrottled("b");

        Assert.Equal(1, (await _cache.GetAsync("a", CancellationToken.None)).Breakdown.Created);
        Assert.Equal(0, (await _cache.GetAsync("a", CancellationToken.None)).Throttled);
        Assert.Equal(1, (await _cache.GetAsync("b", CancellationToken.None)).Throttled);
    }

    /// <summary>An out-of-range status is rejected and creates no entry.</summary>
    [Fact]
    public void RecordOutcome_UnknownStatus_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _cache.RecordOutcome("a", (IngestStatus)99));
    }

    /// <summary>Rule 3: after invalidation the row count is reloaded (picking up a row that committed unnoticed), the counters are kept.</summary>
    [Fact]
    public async Task InvalidateRows_ReloadsTheRowCount_ButKeepsTheUnflushedCounters()
    {
        Existing("a", rows: 5, new StatsCounters(5, 0, 0, 0));
        await _cache.GetAsync("a", CancellationToken.None);
        _cache.RecordOutcome("a", IngestStatus.Updated);          // an unflushed counter that must survive

        _repo.RowsProvider = _ => 6;                              // an insert committed but its outcome was never recorded
        _cache.InvalidateRows("a");
        var stats = await _cache.GetAsync("a", CancellationToken.None);

        Assert.Equal(6, stats.Ingested);                          // reloaded
        Assert.Equal(1, stats.Breakdown.Updated);                 // kept
        Assert.Equal(2, _repo.LoadCount);
    }

    /// <summary>Invalidating a supplier the cache does not know is harmless.</summary>
    [Fact]
    public void InvalidateRows_UnknownSupplier_DoesNothing()
    {
        _cache.InvalidateRows("nobody");
    }

    // ---------- flushing ----------

    /// <summary>A flush writes only suppliers with unflushed increments, and an idle flush writes nothing.</summary>
    [Fact]
    public async Task Flush_WritesOnlySuppliersWithUnflushedIncrements()
    {
        _cache.RecordOutcome("a", IngestStatus.Created);
        _cache.RecordThrottled("b");

        await _cache.FlushAsync(CancellationToken.None);
        await _cache.FlushAsync(CancellationToken.None);          // nothing new: no write
        _cache.RecordOutcome("a", IngestStatus.Unchanged);
        await _cache.FlushAsync(CancellationToken.None);

        Assert.Equal(2, _repo.AddCount);
        Assert.Equal(2, _repo.AddCalls[0].Count);
        var second = Assert.Single(_repo.AddCalls[1]);
        Assert.Equal("a", second.Key);
        Assert.Equal(new StatsCounters(0, 0, 1, 0), second.Value);
    }

    /// <summary>A flush does not change what is reported, only where it is stored.</summary>
    [Fact]
    public async Task Flush_DoesNotChangeTheReportedTotals()
    {
        Existing("a", 3, new StatsCounters(3, 0, 0, 1));
        await _cache.GetAsync("a", CancellationToken.None);
        _cache.RecordOutcome("a", IngestStatus.Created);
        _cache.RecordThrottled("a");
        var before = await _cache.GetAsync("a", CancellationToken.None);

        await _cache.FlushAsync(CancellationToken.None);
        var after = await _cache.GetAsync("a", CancellationToken.None);

        Assert.Equal(before, after);
        Assert.Equal(new StatsCounters(4, 0, 0, 2), _repo.SavedFor("a"));
    }

    /// <summary>A failed flush loses nothing: the increments stay and the next flush saves them exactly once.</summary>
    [Fact]
    public async Task Flush_Failure_KeepsTheIncrements_AndTheRetrySavesThemOnce()
    {
        _cache.RecordThrottled("a");
        _cache.RecordThrottled("a");
        _repo.FailNextAdd = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => _cache.FlushAsync(CancellationToken.None));
        Assert.Equal(2, (await _cache.GetAsync("a", CancellationToken.None)).Throttled);   // still reported
        Assert.Equal(StatsCounters.Zero, _repo.SavedFor("a"));

        await _cache.FlushAsync(CancellationToken.None);

        Assert.Equal(new StatsCounters(0, 0, 0, 2), _repo.SavedFor("a"));
    }

    /// <summary>Increments flushed while the supplier was not loaded are included when it is loaded later, not counted twice.</summary>
    [Fact]
    public async Task Flush_BeforeFirstLoad_ThenLoad_ShowsFlushedPlusNewExactlyOnce()
    {
        _cache.RecordThrottled("a");
        _cache.RecordThrottled("a");
        await _cache.FlushAsync(CancellationToken.None);          // saved 2 while not loaded
        _cache.RecordThrottled("a");                              // unflushed 1

        var stats = await _cache.GetAsync("a", CancellationToken.None);

        Assert.Equal(3, stats.Throttled);
    }

    /// <summary>A new cache over the same database picks up exactly what was flushed (a restart after a graceful stop).</summary>
    [Fact]
    public async Task AfterRestart_AFreshCacheSeesWhatWasFlushed_AndOnlyThat()
    {
        _repo.RowsProvider = _ => 2;
        _cache.RecordOutcome("a", IngestStatus.Created);
        _cache.RecordOutcome("a", IngestStatus.Created);
        _cache.RecordThrottled("a");
        await _cache.FlushAsync(CancellationToken.None);
        _cache.RecordThrottled("a");                              // never flushed: lost, as after a crash

        var restarted = new SupplierStatsCache(_repo, _gate);
        var stats = await restarted.GetAsync("a", CancellationToken.None);

        Assert.Equal(new SupplierStats("a", 2, 1, new IngestBreakdown(2, 0, 0)), stats);
    }

    // ---------- concurrency ----------

    /// <summary>
    /// Many writers, a flusher and a reader at the same time: totals are never lost, never doubled, and what a reader sees
    /// never goes backwards (a flush moves amounts between "unflushed" and "saved" atomically).
    /// </summary>
    [Fact]
    public async Task Concurrent_Recording_Flushing_AndReading_NeverLosesOrDoublesCounts()
    {
        Existing("s", rows: 0, StatsCounters.Zero);
        await _cache.GetAsync("s", CancellationToken.None);       // loaded, so rows are maintained too
        const int workers = 8;
        const int perWorker = 500;
        using var stop = new CancellationTokenSource();

        var flusher = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                await _cache.FlushAsync(CancellationToken.None);
                await Task.Yield();
            }
        });
        var reader = Task.Run(async () =>
        {
            var last = new[] { 0L, 0L, 0L, 0L, 0L };
            while (!stop.IsCancellationRequested)
            {
                var s = await _cache.GetAsync("s", CancellationToken.None);
                var now = new[] { s.Ingested, s.Throttled, s.Breakdown.Created, s.Breakdown.Updated, s.Breakdown.Unchanged };
                for (var i = 0; i < now.Length; i++)
                {
                    Assert.True(now[i] >= last[i], $"value {i} went backwards: {last[i]} -> {now[i]}");
                }

                last = now;
            }
        });
        var writers = Enumerable.Range(0, workers).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < perWorker; i++)
            {
                switch (i % 4)
                {
                    case 0: _cache.RecordOutcome("s", IngestStatus.Created); break;
                    case 1: _cache.RecordOutcome("s", IngestStatus.Updated); break;
                    case 2: _cache.RecordOutcome("s", IngestStatus.Unchanged); break;
                    default: _cache.RecordThrottled("s"); break;
                }
            }
        })).ToArray();

        await Task.WhenAll(writers);
        stop.Cancel();
        await Task.WhenAll(flusher, reader);
        await _cache.FlushAsync(CancellationToken.None);

        const int each = workers * perWorker / 4;
        Assert.Equal(new StatsCounters(each, each, each, each), _repo.SavedFor("s"));
        var final = await _cache.GetAsync("s", CancellationToken.None);
        Assert.Equal(new SupplierStats("s", each, each, new IngestBreakdown(each, each, each)), final);
    }
}
