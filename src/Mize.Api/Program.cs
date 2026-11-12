using System.Text.Json;
using System.Text.Json.Serialization;
using Mize.Api.Data;
using Mize.Api.Infrastructure;
using Mize.Api.RateLimiting;
using Mize.Api.Services;
using Mize.Api.Stats;
using Mize.Api.Validation;

var builder = WebApplication.CreateBuilder(args);

// Logging: built-in ILogger with structured JSON on the console. This is the ONLY place that names a logging
// provider; every class depends on ILogger<T>, so switching to e.g. Serilog means changing just these lines.
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();

// MVC controllers; enums serialise as camelCase strings ("created"); binding failures are logged at Warning.
builder.Services
    .AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)))
    .ConfigureApiBehaviorOptions(o => o.InvalidModelStateResponseFactory = ApiBehaviorConfiguration.CreateInvalidModelStateResponse);

// Turns unhandled exceptions into RFC 7807 500 responses (the middleware logs them at Error).
builder.Services.AddProblemDetails();

// Rate-limit settings from the "RateLimit" section (defaults: 100 requests / 60 s).
builder.Services.AddOptions<RateLimitOptions>().Bind(builder.Configuration.GetSection(RateLimitOptions.SectionName));

// Clock (replaceable in tests). The connection string is read lazily so tests can override configuration.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IDbConnectionFactory>(sp =>
    new SqliteConnectionFactory(sp.GetRequiredService<IConfiguration>().GetConnectionString("Reservations")
        ?? throw new InvalidOperationException("Connection string 'Reservations' is not configured.")));

// Singletons: all of these are stateless or deliberately hold process-wide state (limiter, stats, write gate).
builder.Services.AddSingleton<IDatabaseInitializer, DatabaseInitializer>();
builder.Services.AddSingleton<IReservationRepository, ReservationRepository>();
builder.Services.AddSingleton<IWriteGate, WriteGate>();
builder.Services.AddSingleton<IStatsRepository, StatsRepository>();
builder.Services.AddSingleton<IReservationService, ReservationService>();
builder.Services.AddSingleton<IRateLimiter, SlidingWindowRateLimiter>();

// Statistics: an in-memory cache over SQL (one cache instance serving both roles), flushed by a background service
// every Stats:FlushIntervalSeconds and on graceful shutdown.
builder.Services.AddOptions<StatsOptions>().Bind(builder.Configuration.GetSection(StatsOptions.SectionName));
builder.Services.AddSingleton<SupplierStatsCache>();
builder.Services.AddSingleton<ISupplierStatsTracker>(sp => sp.GetRequiredService<SupplierStatsCache>());
builder.Services.AddSingleton<IStatsFlusher>(sp => sp.GetRequiredService<SupplierStatsCache>());
builder.Services.AddHostedService<StatsFlushService>();
builder.Services.AddSingleton<ReservationRequestValidator>();

var app = builder.Build();

app.UseExceptionHandler();
app.MapControllers();

// Create the schema before serving the first request.
await app.Services.GetRequiredService<IDatabaseInitializer>().InitializeAsync();

app.Run();

/// <summary>Makes the auto-generated Program class visible to <c>WebApplicationFactory</c> in the test project.</summary>
public partial class Program;
