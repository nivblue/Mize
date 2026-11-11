using Dapper;

namespace Mize.Api.Data;

/// <summary>Prepares the database schema at application start.</summary>
public interface IDatabaseInitializer
{
    /// <summary>Creates any missing tables; safe to call repeatedly.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

/// <summary>Creates the <c>Reservations</c> table using <see cref="SqlQueries.CreateReservationsTable"/>.</summary>
public sealed class DatabaseInitializer : IDatabaseInitializer
{
    private readonly IDbConnectionFactory _connectionFactory;

    /// <summary>Creates the initializer.</summary>
    /// <param name="connectionFactory">Source of database connections.</param>
    public DatabaseInitializer(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(SqlQueries.CreateReservationsTable, cancellationToken: cancellationToken));
    }
}
