using Microsoft.Data.Sqlite;
using Mize.Api.Data;

namespace Mize.Tests.Helpers;

/// <summary>A throw-away SQLite database file in the temp folder, deleted on dispose.</summary>
public sealed class TempDatabase : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mize-tests-" + Guid.NewGuid().ToString("N"));

    /// <summary>Creates the temp location (the file itself is created lazily by SQLite).</summary>
    public TempDatabase()
    {
        FilePath = Path.Combine(_directory, "test.db");
        ConnectionString = $"Data Source={FilePath}";
        ConnectionFactory = new SqliteConnectionFactory(ConnectionString);
    }

    /// <summary>Full path of the database file.</summary>
    public string FilePath { get; }

    /// <summary>SQLite connection string pointing at <see cref="FilePath"/>.</summary>
    public string ConnectionString { get; }

    /// <summary>Connection factory for the temp database.</summary>
    public SqliteConnectionFactory ConnectionFactory { get; }

    /// <summary>Creates the Reservations table.</summary>
    public Task InitializeSchemaAsync() => new DatabaseInitializer(ConnectionFactory).InitializeAsync();

    /// <summary>Releases pooled connections (so the file can be deleted) and removes the folder.</summary>
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
