using Dapper;
using Mize.Api.Stats;

namespace Mize.Api.Data;

/// <summary>Dapper implementation of <see cref="IStatsRepository"/>.</summary>
public sealed class StatsRepository : IStatsRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    /// <summary>The raw result of <see cref="SqlQueries.LoadSupplierStats"/>; the counters are null when no stats row exists.</summary>
    private sealed class StatsRow
    {
        /// <summary>Column ReservationCount.</summary>
        public long ReservationCount { get; set; }

        /// <summary>Column Created (null when no stats row exists).</summary>
        public long? Created { get; set; }

        /// <summary>Column Updated (null when no stats row exists).</summary>
        public long? Updated { get; set; }

        /// <summary>Column Unchanged (null when no stats row exists).</summary>
        public long? Unchanged { get; set; }

        /// <summary>Column Throttled (null when no stats row exists).</summary>
        public long? Throttled { get; set; }
    }

    /// <summary>Creates the repository.</summary>
    /// <param name="connectionFactory">Source of open connections; one is opened per operation.</param>
    public StatsRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    /// <inheritdoc />
    public async Task<StoredSupplierStats> LoadAsync(string supplierId, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleAsync<StatsRow>(new CommandDefinition(
            SqlQueries.LoadSupplierStats,
            new { SupplierId = supplierId },
            cancellationToken: cancellationToken));

        var counters = row.Created is null
            ? null
            : new StatsCounters(row.Created.Value, row.Updated ?? 0, row.Unchanged ?? 0, row.Throttled ?? 0);
        return new StoredSupplierStats(row.ReservationCount, counters);
    }

    /// <inheritdoc />
    public async Task AddAsync(IReadOnlyDictionary<string, StatsCounters> deltas, CancellationToken cancellationToken)
    {
        if (deltas.Count == 0)
        {
            return;
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var (supplierId, delta) in deltas)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                SqlQueries.AddSupplierStatsDelta,
                new { SupplierId = supplierId, delta.Created, delta.Updated, delta.Unchanged, delta.Throttled },
                transaction,
                cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
