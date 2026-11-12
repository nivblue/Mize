using Microsoft.Data.Sqlite;

namespace Mize.E2ETests;

/// <summary>One row as stored in the API's SQLite file (only the columns the tests check).</summary>
/// <param name="RoomId">Stored room id.</param>
/// <param name="Price">Stored price, parsed from its text form.</param>
/// <param name="UpdatedAtUtc">Stored updatedAtUtc, parsed from its ISO-8601 text form.</param>
public sealed record StoredRow(string RoomId, decimal Price, DateTimeOffset UpdatedAtUtc);

/// <summary>The counters the API saved for a supplier (the SupplierStats table).</summary>
/// <param name="Created">Saved created count.</param>
/// <param name="Updated">Saved updated count.</param>
/// <param name="Unchanged">Saved unchanged (duplicate) count.</param>
/// <param name="Throttled">Saved throttled count.</param>
public sealed record StoredStats(long Created, long Updated, long Unchanged, long Throttled);

/// <summary>
/// Shared setup for E2E test classes: an HttpClient pointed at the running API, a way to wait until the API is up,
/// and a read-only view of the API's SQLite file. Configuration comes from environment variables:
/// <c>E2E_BASE_URL</c> (default http://localhost:8080) and <c>E2E_DATABASE_PATH</c> (path of the shared SQLite file).
/// </summary>
public sealed class E2EFixture : IAsyncLifetime
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(60);

    /// <summary>HTTP client for the API under test.</summary>
    public HttpClient Client { get; private set; } = null!;

    /// <summary>Path of the API's SQLite file as seen from the test process; null when not configured.</summary>
    public string? DatabasePath { get; private set; }

    /// <summary>Creates the client and blocks until the API answers, so tests never race the container start-up.</summary>
    public async Task InitializeAsync()
    {
        var baseUrl = Environment.GetEnvironmentVariable("E2E_BASE_URL") ?? "http://localhost:8080";
        DatabasePath = Environment.GetEnvironmentVariable("E2E_DATABASE_PATH");
        Client = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };

        await WaitUntilReadyAsync(baseUrl);
    }

    /// <summary>Disposes the HTTP client.</summary>
    public Task DisposeAsync()
    {
        Client.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Returns a supplier id nobody else uses, so tests never share rate-limit windows or stats.</summary>
    public static string NewSupplierId() => "e2e-" + Guid.NewGuid().ToString("N");

    /// <summary>
    /// Reads one reservation straight from the SQLite file (read-only, no connection pooling), retrying briefly
    /// if the API holds a lock at that moment.
    /// </summary>
    /// <param name="supplierId">Supplier id.</param>
    /// <param name="reservationId">Reservation id.</param>
    /// <returns>The stored row, or null when no such reservation exists.</returns>
    public Task<StoredRow?> ReadRowAsync(string supplierId, string reservationId) =>
        QueryAsync(
            "SELECT RoomId, Price, UpdatedAtUtc FROM Reservations WHERE SupplierId = $s AND ReservationId = $r;",
            new() { ["$s"] = supplierId, ["$r"] = reservationId },
            reader => new StoredRow(
                reader.GetString(0),
                decimal.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>
    /// Reads the counters the API has flushed for a supplier into the SupplierStats table (read-only).
    /// </summary>
    /// <param name="supplierId">Supplier id.</param>
    /// <returns>The saved counters, or null when nothing has been flushed for the supplier yet.</returns>
    public Task<StoredStats?> ReadStatsRowAsync(string supplierId) =>
        QueryAsync(
            "SELECT Created, Updated, Unchanged, Throttled FROM SupplierStats WHERE SupplierId = $s;",
            new() { ["$s"] = supplierId },
            reader => new StoredStats(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3)));

    /// <summary>Waits until a supplier's flushed counters satisfy a condition (the API flushes every few seconds), or fails after the timeout.</summary>
    /// <param name="supplierId">Supplier id.</param>
    /// <param name="condition">What the saved counters must satisfy.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <returns>The saved counters that satisfied the condition.</returns>
    public async Task<StoredStats> WaitForSavedStatsAsync(string supplierId, Func<StoredStats, bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var saved = await ReadStatsRowAsync(supplierId);
            if (saved is not null && condition(saved))
            {
                return saved;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"The statistics of '{supplierId}' were not flushed as expected within {timeout.TotalSeconds}s; last saved: {saved}");
            }

            await Task.Delay(500);
        }
    }

    /// <summary>Runs a read-only query against the SQLite file, retrying briefly when the API holds a lock, and maps the first row.</summary>
    private async Task<T?> QueryAsync<T>(string sql, Dictionary<string, object> parameters, Func<SqliteDataReader, T> map) where T : class
    {
        if (string.IsNullOrEmpty(DatabasePath))
        {
            throw new InvalidOperationException("E2E_DATABASE_PATH is not set; cannot inspect the SQLite file.");
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var connection = new SqliteConnection($"Data Source={DatabasePath};Mode=ReadOnly;Pooling=False");
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                foreach (var (name, value) in parameters)
                {
                    command.Parameters.AddWithValue(name, value);
                }

                await using var reader = await command.ExecuteReaderAsync();
                return await reader.ReadAsync() ? map(reader) : null;
            }
            catch (SqliteException) when (attempt < 5)
            {
                await Task.Delay(200);
            }
        }
    }

    /// <summary>Polls the stats endpoint until the API answers 200 or the timeout elapses.</summary>
    private async Task WaitUntilReadyAsync(string baseUrl)
    {
        var deadline = DateTime.UtcNow + ReadyTimeout;
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var response = await Client.GetAsync("/api/reservations/stats/readiness-probe");
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException ex)
            {
                last = ex;
            }

            await Task.Delay(500);
        }

        throw new TimeoutException($"The API at {baseUrl} did not become ready within {ReadyTimeout.TotalSeconds}s.", last);
    }
}
