using System.Data;
using Mize.Api.Data;
using Mize.Tests.Helpers;

namespace Mize.Tests.Unit;

/// <summary>Unit tests for <see cref="SqliteConnectionFactory"/>.</summary>
public class SqliteConnectionFactoryTests
{
    /// <summary>The factory returns an already-open connection.</summary>
    [Fact]
    public async Task OpenConnectionAsync_ReturnsOpenConnection()
    {
        using var db = new TempDatabase();

        await using var connection = await db.ConnectionFactory.OpenConnectionAsync(CancellationToken.None);

        Assert.Equal(ConnectionState.Open, connection.State);
    }

    /// <summary>A missing parent directory of the database file is created.</summary>
    [Fact]
    public void Constructor_CreatesMissingDirectory()
    {
        using var db = new TempDatabase();

        Assert.True(Directory.Exists(Path.GetDirectoryName(db.FilePath)));
    }

    /// <summary>An in-memory data source needs no directory and does not throw.</summary>
    [Fact]
    public async Task Constructor_InMemoryDataSource_Works()
    {
        var factory = new SqliteConnectionFactory("Data Source=:memory:");

        await using var connection = await factory.OpenConnectionAsync(CancellationToken.None);

        Assert.Equal(ConnectionState.Open, connection.State);
    }

    /// <summary>An empty data source (SQLite then uses a private temporary database) needs no directory and works.</summary>
    [Fact]
    public async Task Constructor_EmptyDataSource_Works()
    {
        var factory = new SqliteConnectionFactory("Data Source=");

        await using var connection = await factory.OpenConnectionAsync(CancellationToken.None);

        Assert.Equal(ConnectionState.Open, connection.State);
    }

    /// <summary>When the database cannot be opened the error is thrown to the caller (the connection is cleaned up, not leaked).</summary>
    [Fact]
    public async Task OpenConnectionAsync_WhenTheDatabaseCannotBeOpened_Throws()
    {
        using var db = new TempDatabase();
        var directoryAsDatabase = Path.GetDirectoryName(db.FilePath)!;   // a folder is not a database file
        var factory = new SqliteConnectionFactory($"Data Source={directoryAsDatabase}");

        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => factory.OpenConnectionAsync(CancellationToken.None));
    }
}
