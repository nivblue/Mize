using Microsoft.Extensions.Logging;
using Moq;
using Mize.Api.Data;
using Mize.Api.Models;
using Mize.Api.Services;
using Mize.Api.Stats;
using Mize.Tests.Helpers;

namespace Mize.Tests.Unit;

/// <summary>Unit tests for <see cref="ReservationService"/>, with the repository and the statistics mocked.</summary>
public class ReservationServiceTests
{
    private readonly Mock<IReservationRepository> _repo = new();
    private readonly Mock<ISupplierStatsTracker> _stats = new();
    private readonly TrackingGate _gate = new();
    private readonly ListLogger<ReservationService> _logger = new();
    private readonly ReservationService _service;

    /// <summary>A write gate that remembers whether it is currently held, to prove work happens inside it.</summary>
    private sealed class TrackingGate : IWriteGate
    {
        /// <summary>True between entering and releasing the gate.</summary>
        public bool IsHeld { get; private set; }

        /// <inheritdoc />
        public Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
        {
            IsHeld = true;
            return Task.FromResult<IDisposable>(new Handle(this));
        }

        /// <summary>Releases the gate when disposed.</summary>
        private sealed class Handle : IDisposable
        {
            private readonly TrackingGate _owner;

            /// <summary>Creates a handle for the gate.</summary>
            public Handle(TrackingGate owner)
            {
                _owner = owner;
            }

            /// <summary>Marks the gate as released.</summary>
            public void Dispose() => _owner.IsHeld = false;
        }
    }

    /// <summary>Creates the service under test.</summary>
    public ReservationServiceTests()
    {
        _service = new ReservationService(_repo.Object, _gate, _stats.Object, _logger);
    }

    /// <summary>Makes the mocked repository return <paramref name="existing"/> for any lookup.</summary>
    private void StoredIs(Reservation? existing) =>
        _repo.Setup(r => r.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

    /// <summary>A new reservation is inserted, never updated, and logged at Information.</summary>
    [Fact]
    public async Task Ingest_NewReservation_Inserts_AndLogsInformation()
    {
        StoredIs(null);
        var expected = TestData.Reservation();

        var status = await _service.IngestAsync(TestData.Request(), CancellationToken.None);

        Assert.Equal(IngestStatus.Created, status);
        _repo.Verify(r => r.InsertAsync(expected, It.IsAny<CancellationToken>()), Times.Once);
        _repo.Verify(r => r.UpdateAsync(It.IsAny<Reservation>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(LogLevel.Information, Assert.Single(_logger.Entries).Level);
    }

    /// <summary>An identical duplicate writes nothing and is logged at Information.</summary>
    [Fact]
    public async Task Ingest_IdenticalReservation_ReturnsUnchanged_AndDoesNotWrite()
    {
        StoredIs(TestData.Reservation());

        var status = await _service.IngestAsync(TestData.Request(), CancellationToken.None);

        Assert.Equal(IngestStatus.Unchanged, status);
        _repo.Verify(r => r.InsertAsync(It.IsAny<Reservation>(), It.IsAny<CancellationToken>()), Times.Never);
        _repo.Verify(r => r.UpdateAsync(It.IsAny<Reservation>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(LogLevel.Information, Assert.Single(_logger.Entries).Level);
    }

    /// <summary>Different details with a newer updatedAtUtc update the stored record.</summary>
    [Fact]
    public async Task Ingest_ChangedDetailsAndNewerTimestamp_Updates()
    {
        StoredIs(TestData.Reservation(price: 450m));
        var newer = TestData.Request(price: 500m, updatedAtUtc: TestData.UpdatedAt.AddHours(1));

        var status = await _service.IngestAsync(newer, CancellationToken.None);

        Assert.Equal(IngestStatus.Updated, status);
        _repo.Verify(r => r.UpdateAsync(
            It.Is<Reservation>(x => x.Price == 500m), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(LogLevel.Information, Assert.Single(_logger.Entries).Level);
    }

    /// <summary>Different details with the SAME updatedAtUtc still update (the timestamp never blocks an update).</summary>
    [Fact]
    public async Task Ingest_ChangedDetailsSameTimestamp_Updates()
    {
        StoredIs(TestData.Reservation(price: 450m));

        var status = await _service.IngestAsync(TestData.Request(price: 500m), CancellationToken.None);

        Assert.Equal(IngestStatus.Updated, status);
    }

    /// <summary>
    /// Different details with an OLDER updatedAtUtc are still applied (the spec: different details update the record),
    /// and the out-of-order timestamp is logged at WARNING.
    /// </summary>
    [Fact]
    public async Task Ingest_OlderTimestampWithDifferentDetails_StillUpdates_AndLogsWarning()
    {
        StoredIs(TestData.Reservation(price: 450m));
        var older = TestData.Request(price: 999m, updatedAtUtc: TestData.UpdatedAt.AddMinutes(-5));

        var status = await _service.IngestAsync(older, CancellationToken.None);

        Assert.Equal(IngestStatus.Updated, status);
        _repo.Verify(r => r.UpdateAsync(It.Is<Reservation>(x => x.Price == 999m), It.IsAny<CancellationToken>()), Times.Once);
        _repo.Verify(r => r.InsertAsync(It.IsAny<Reservation>(), It.IsAny<CancellationToken>()), Times.Never);
        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("out-of-order", entry.Message);
        Assert.Contains("res-1", entry.Message);
        Assert.Contains("supplier-1", entry.Message);
    }

    // ---------- statistics: the strict rules ----------

    /// <summary>Each outcome is recorded once, for the right supplier.</summary>
    [Fact]
    public async Task Ingest_RecordsEachOutcomeInTheStatistics()
    {
        StoredIs(null);
        await _service.IngestAsync(TestData.Request(), CancellationToken.None);
        StoredIs(TestData.Reservation());
        await _service.IngestAsync(TestData.Request(), CancellationToken.None);
        await _service.IngestAsync(TestData.Request(price: 1m), CancellationToken.None);

        _stats.Verify(s => s.RecordOutcome("supplier-1", IngestStatus.Created), Times.Once);
        _stats.Verify(s => s.RecordOutcome("supplier-1", IngestStatus.Unchanged), Times.Once);
        _stats.Verify(s => s.RecordOutcome("supplier-1", IngestStatus.Updated), Times.Once);
    }

    /// <summary>Rule 2: the created outcome is recorded only after the insert, and while the write gate is still held.</summary>
    [Fact]
    public async Task Ingest_Created_IsRecordedAfterTheInsert_InsideTheGate()
    {
        StoredIs(null);
        var order = new List<string>();
        var heldWhenRecorded = false;
        _repo.Setup(r => r.InsertAsync(It.IsAny<Reservation>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("insert")).Returns(Task.CompletedTask);
        _stats.Setup(s => s.RecordOutcome(It.IsAny<string>(), It.IsAny<IngestStatus>()))
            .Callback(() => { order.Add("record"); heldWhenRecorded = _gate.IsHeld; });

        await _service.IngestAsync(TestData.Request(), CancellationToken.None);

        Assert.Equal(new[] { "insert", "record" }, order);
        Assert.True(heldWhenRecorded);
        Assert.False(_gate.IsHeld);   // released afterwards
    }

    /// <summary>Rule 3: when the insert throws (it may have committed), the cached row count is invalidated and nothing is recorded.</summary>
    [Fact]
    public async Task Ingest_InsertThrows_InvalidatesTheRowCount_AndRecordsNothing()
    {
        StoredIs(null);
        _repo.Setup(r => r.InsertAsync(It.IsAny<Reservation>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("connection dropped"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.IngestAsync(TestData.Request(), CancellationToken.None));

        _stats.Verify(s => s.InvalidateRows("supplier-1"), Times.Once);
        _stats.Verify(s => s.RecordOutcome(It.IsAny<string>(), It.IsAny<IngestStatus>()), Times.Never);
        Assert.False(_gate.IsHeld);
    }

    /// <summary>An update does not change the row count, so a failing update neither invalidates nor records.</summary>
    [Fact]
    public async Task Ingest_UpdateThrows_DoesNotInvalidate_AndRecordsNothing()
    {
        StoredIs(TestData.Reservation(price: 450m));
        _repo.Setup(r => r.UpdateAsync(It.IsAny<Reservation>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.IngestAsync(TestData.Request(price: 1m), CancellationToken.None));

        _stats.Verify(s => s.InvalidateRows(It.IsAny<string>()), Times.Never);
        _stats.Verify(s => s.RecordOutcome(It.IsAny<string>(), It.IsAny<IngestStatus>()), Times.Never);
    }

    /// <summary>A failing lookup means nothing was written: no invalidation, nothing recorded, and the gate is released.</summary>
    [Fact]
    public async Task Ingest_LookupThrows_Propagates_ReleasesTheGate_AndRecordsNothing()
    {
        _repo.SetupSequence(r => r.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"))
            .ReturnsAsync((Reservation?)null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.IngestAsync(TestData.Request(), CancellationToken.None));
        Assert.False(_gate.IsHeld);
        _stats.Verify(s => s.InvalidateRows(It.IsAny<string>()), Times.Never);

        // A second call must not deadlock on the gate.
        var status = await _service.IngestAsync(TestData.Request(), CancellationToken.None);
        Assert.Equal(IngestStatus.Created, status);
    }

    /// <summary>Concurrent identical requests produce exactly one insert thanks to the write gate.</summary>
    [Fact]
    public async Task Ingest_ConcurrentDuplicates_InsertOnlyOnce()
    {
        var repo = new InMemoryReservationRepository();
        var service = new ReservationService(repo, new WriteGate(), new Mock<ISupplierStatsTracker>().Object, new ListLogger<ReservationService>());

        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => service.IngestAsync(TestData.Request(), CancellationToken.None))));

        Assert.Equal(1, results.Count(s => s == IngestStatus.Created));
        Assert.Equal(19, results.Count(s => s == IngestStatus.Unchanged));
        Assert.Equal(1, repo.WriteCount);
    }
}
