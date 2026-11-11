using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mize.Api.Data;
using Mize.Api.RateLimiting;
using Mize.Api.Services;

namespace Mize.Tests.Helpers;

/// <summary>
/// Boots the real API in memory with test-friendly seams: a fake clock, captured logs, a temp SQLite database
/// and optional overrides for the rate limit, repository or service. Used by API and integration tests.
/// </summary>
public sealed class MizeApiFactory : WebApplicationFactory<Program>
{
    private readonly TempDatabase? _ownedDatabase;

    /// <summary>Creates a factory with its own temp database, or one at <paramref name="databasePath"/> that outlives the factory.</summary>
    /// <param name="databasePath">Existing database path to reuse (e.g. to simulate a restart); null for a fresh temp database.</param>
    public MizeApiFactory(string? databasePath = null)
    {
        if (databasePath is null)
        {
            _ownedDatabase = new TempDatabase();
            DatabasePath = _ownedDatabase.FilePath;
        }
        else
        {
            DatabasePath = databasePath;
        }
    }

    /// <summary>Path of the SQLite file the API uses.</summary>
    public string DatabasePath { get; }

    /// <summary>Captures every log written by the running API.</summary>
    public ListLoggerProvider Logs { get; } = new();

    /// <summary>The clock the API uses; advance it to move the rate-limit window.</summary>
    public FakeTimeProvider Time { get; } = new();

    /// <summary>When set, replaces the real repository.</summary>
    public IReservationRepository? RepositoryOverride { get; init; }

    /// <summary>When set, overrides <c>RateLimit:MaxRequests</c> (default is the real 100).</summary>
    public int? MaxRequests { get; init; }

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Time);

            services.RemoveAll<IDbConnectionFactory>();
            services.AddSingleton<IDbConnectionFactory>(new SqliteConnectionFactory($"Data Source={DatabasePath}"));

            if (RepositoryOverride is not null)
            {
                services.RemoveAll<IReservationRepository>();
                services.AddSingleton(RepositoryOverride);
            }

            if (MaxRequests is { } max)
            {
                services.Configure<RateLimitOptions>(o => o.MaxRequests = max);
            }
        });
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            SqliteConnection.ClearAllPools();
            _ownedDatabase?.Dispose();
        }
    }
}
