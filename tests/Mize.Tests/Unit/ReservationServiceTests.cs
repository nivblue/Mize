using Microsoft.Extensions.Logging;
using Moq;
using Mize.Api.Data;
using Mize.Api.Models;
using Mize.Api.Services;
using Mize.Tests.Helpers;

namespace Mize.Tests.Unit;

/// <summary>Unit tests for <see cref="ReservationService"/>, with the repository mocked.</summary>
public class ReservationServiceTests
{
    private readonly Mock<IReservationRepository> _repo = new();
    private readonly ListLogger<ReservationService> _logger = new();
    private readonly ReservationService _service;

    /// <summary>Creates the service under test.</summary>
    public ReservationServiceTests()
    {
        _service = new ReservationService(_repo.Object, _logger);
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

    /// <summary>Different details with the SAME updatedAtUtc still update (only strictly older is stale).</summary>
    [Fact]
    public async Task Ingest_ChangedDetailsSameTimestamp_Updates()
    {
        StoredIs(TestData.Reservation(price: 450m));

        var status = await _service.IngestAsync(TestData.Request(price: 500m), CancellationToken.None);

        Assert.Equal(IngestStatus.Updated, status);
    }

    /// <summary>An older updatedAtUtc is ignored (no write) and logged at WARNING.</summary>
    [Fact]
    public async Task Ingest_OlderTimestamp_ReturnsStale_DoesNotWrite_AndLogsWarning()
    {
        StoredIs(TestData.Reservation());
        var older = TestData.Request(price: 999m, updatedAtUtc: TestData.UpdatedAt.AddMinutes(-5));

        var status = await _service.IngestAsync(older, CancellationToken.None);

        Assert.Equal(IngestStatus.Stale, status);
        _repo.Verify(r => r.InsertAsync(It.IsAny<Reservation>(), It.IsAny<CancellationToken>()), Times.Never);
        _repo.Verify(r => r.UpdateAsync(It.IsAny<Reservation>(), It.IsAny<CancellationToken>()), Times.Never);
        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("res-1", entry.Message);
        Assert.Contains("supplier-1", entry.Message);
    }

    /// <summary>Repository failures propagate (the API turns them into HTTP 500) and the write gate is released.</summary>
    [Fact]
    public async Task Ingest_RepositoryThrows_Propagates_AndReleasesGate()
    {
        _repo.SetupSequence(r => r.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"))
            .ReturnsAsync((Reservation?)null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.IngestAsync(TestData.Request(), CancellationToken.None));

        // A second call must not deadlock on the gate.
        var status = await _service.IngestAsync(TestData.Request(), CancellationToken.None);
        Assert.Equal(IngestStatus.Created, status);
    }

    /// <summary>Concurrent identical requests produce exactly one insert thanks to the write gate.</summary>
    [Fact]
    public async Task Ingest_ConcurrentDuplicates_InsertOnlyOnce()
    {
        var repo = new InMemoryReservationRepository();
        var service = new ReservationService(repo, new ListLogger<ReservationService>());

        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => service.IngestAsync(TestData.Request(), CancellationToken.None))));

        Assert.Equal(1, results.Count(s => s == IngestStatus.Created));
        Assert.Equal(19, results.Count(s => s == IngestStatus.Unchanged));
        Assert.Equal(1, repo.WriteCount);
    }
}
