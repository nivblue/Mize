using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace Mize.Api.Data;

/// <summary>SQLite implementation of <see cref="IDbConnectionFactory"/>.</summary>
public sealed class SqliteConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;

    /// <summary>
    /// Creates the factory and makes sure the database file's directory exists
    /// (SQLite creates the file itself but not its parent folder).
    /// </summary>
    /// <param name="connectionString">A SQLite connection string, e.g. <c>Data Source=data/mize.db</c>.</param>
    public SqliteConnectionFactory(string connectionString)
    {
        _connectionString = connectionString;

        var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
        var isInMemory = string.IsNullOrEmpty(dataSource) || dataSource.Contains(":memory:", StringComparison.OrdinalIgnoreCase);
        var directory = isInMemory ? null : Path.GetDirectoryName(Path.GetFullPath(dataSource));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    /// <inheritdoc />
    public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
