# MIZE — Reservation Ingest API

An ASP.NET Core (.NET 8) Web API that ingests supplier reservations into SQLite (via Dapper), de-duplicates them,
applies a per-supplier rolling rate limit, and reports ingest/throttle statistics.

## Contents
1. [Quick start](#quick-start)
2. [Endpoints](#endpoints)
3. [Behaviour rules](#behaviour-rules)
4. [Design decisions](#design-decisions)
5. [Project layout](#project-layout)
6. [Logging](#logging)
7. [Configuration](#configuration)
8. [Database](#database)
9. [Dependencies](#dependencies)
10. [Tests](#tests)
11. [Known limitations](#known-limitations)
12. [Optional future work](#optional-future-work)

## Quick start

Requirements: Docker with Compose. **No .NET SDK or SQLite installation is needed.**

```bash
docker compose up --build          # API on http://localhost:8080, database in ./data/mize.db
docker compose run --rm tests      # run all tests inside the .NET SDK image
```

Try it:

```bash
curl -i -X POST http://localhost:8080/api/reservations/ingest \
  -H 'Content-Type: application/json' \
  -d '{"supplierId":"acme","reservationId":"r1","roomId":"101",
       "checkIn":"2026-08-01T00:00:00Z","checkOut":"2026-08-03T00:00:00Z",
       "price":450.00,"updatedAtUtc":"2026-07-01T10:15:00Z"}'

curl http://localhost:8080/api/reservations/stats/acme
```

## Endpoints

### `POST /api/reservations/ingest`

Body (all fields required; all timestamps must be UTC):

```json
{
  "supplierId": "string",
  "reservationId": "string",
  "roomId": "string",
  "checkIn": "2026-08-01T00:00:00Z",
  "checkOut": "2026-08-03T00:00:00Z",
  "price": 450.00,
  "updatedAtUtc": "2026-07-01T10:15:00Z"
}
```

| HTTP | `status` in body | Meaning |
|------|------------------|---------|
| 201 | `created` | New reservation stored. |
| 200 | `updated` | Same id, different details: record updated. |
| 200 | `unchanged` | Identical to what is stored: **nothing written**; message says "nothing changed". |
| 200 | `stale` | Same id but older `updatedAtUtc` than stored: ignored, nothing changed. |
| 400 | – | Validation failure (RFC 7807 `ValidationProblemDetails` listing the fields). |
| 415 | – | Content type is not JSON. |
| 429 | – | Supplier exceeded the rate limit. Includes a `Retry-After` header (seconds). |
| 500 | – | Unexpected error (RFC 7807 problem response). |

Success body: `{ "status": "created", "supplierId": "...", "reservationId": "...", "message": "..." }`

### `GET /api/reservations/stats/{supplierId}`

```json
{
  "supplierId": "acme",
  "ingested": 99,
  "throttled": 9,
  "breakdown": { "created": 98, "updated": 0, "unchanged": 1, "stale": 1 }
}
```

* `ingested` = `created + updated + unchanged` (accepted requests that were ingested). `stale` is reported in the breakdown only.
* `throttled` = requests rejected with 429.
* An unknown supplier returns **200 with zeros** (not 404). The supplier id is case-sensitive.

## Behaviour rules

* **Identity:** a reservation is unique per `(supplierId, reservationId)`; two suppliers may reuse an id.
* **Exact duplicate:** all of room, check-in, check-out, price and `updatedAtUtc` equal → nothing is written (`unchanged`).
* **Changed details:** update in place (`updated`) when `updatedAtUtc` is equal or newer.
* **Out-of-order update:** `updatedAtUtc` strictly older than stored → ignored (`stale`) and logged at **Warning**.
* **Rate limit:** more than 100 *accepted* requests from one supplier within any rolling 60 seconds → further requests get 429 until the window clears.
  * The window is exact and rolling (a queue of timestamps per supplier), not fixed buckets.
  * Rejected (429) requests are **not** counted, so a supplier recovers as soon as old requests age out.
  * Invalid (400) requests are rejected **before** the limiter and do not consume quota.
  * `Retry-After` = seconds until the oldest request in the window expires (rounded up, minimum 1).
* **Validation (strict):** `supplierId`, `reservationId`, `roomId` non-empty; `checkIn`, `checkOut`, `updatedAtUtc` have offset `+00:00`/`Z`; `checkOut` after `checkIn`; `price >= 0`. Missing JSON properties, malformed JSON and wrong types are also 400.

## Design decisions

Each choice below was made together with the project owner.

| Decision | Choice | Why |
|----------|--------|-----|
| Rate-limit & stats state | In-memory | Simple and fast; exact rolling window. Trade-off: resets on restart, single instance only. |
| Throttled requests and the window | Rejected requests don't count; 429 + `Retry-After` | Supplier recovers as soon as the window clears; standard HTTP semantics. |
| Response codes | 201 / 200 / 200 with a `status` field | Makes "nothing changed" explicit in the body. |
| Stale update | Ignore + Warning, status `stale` | Late-arriving messages must not overwrite newer data. |
| Identity & stats | Per `(supplierId, reservationId)`; ingested = created+updated+unchanged | Avoids cross-supplier collisions. |
| Validation | Strict | Bad data never reaches the database. |
| Solution layout | One API project + one test project | Compact; right-sized for this scope. |
| Logging | Built-in `ILogger` with JSON console | No extra dependency (stability); swappable, see [Logging](#logging). |
| Assertions | Plain xUnit `Assert` | No extra package or license concern. |
| Swagger | Skipped for now | See [Optional future work](#optional-future-work). |
| Build/test tooling | Docker SDK image | No local .NET install required. |

Implementation choices made along the way:

* **Controllers** (not Minimal APIs): clean separation and straightforward unit testing of actions.
* **`TimeProvider`** (built into .NET 8) for the clock, so the rolling window is tested without sleeping.
* **Write gate:** `ReservationService` serialises read-decide-write with a `SemaphoreSlim`, so concurrent duplicates cannot both insert (which would violate the primary key). Adequate for one process on one SQLite file.
* **SQLite storage types:** dates are stored as ISO-8601 UTC text and price as invariant decimal text, because SQLite has no datetime/decimal type; this round-trips values exactly.
* **Journal mode:** SQLite's default (not WAL), because WAL is unreliable on Docker bind mounts from macOS.
* **Errors:** `UseExceptionHandler` + `AddProblemDetails` turn unhandled exceptions into 500 problem responses (and log them at Error).

## Project layout

```
Mize.sln
Dockerfile                      multi-stage build (SDK -> ASP.NET runtime)
docker-compose.yml              `api` service (+ ./data mount) and an on-demand `tests` service
MIZE.md                         this file
src/Mize.Api/
  Program.cs                    composition root: DI, logging, middleware, schema init
  Controllers/ReservationsController.cs   the two endpoints (validate -> limit -> ingest -> stats)
  Models/                       ReservationRequest, Reservation, IngestStatus, IngestResponse, SupplierStats
  Validation/ReservationRequestValidator.cs
  RateLimiting/                 IRateLimiter, SlidingWindowRateLimiter, RateLimitOptions
  Stats/                        ISupplierStatsTracker, InMemorySupplierStatsTracker
  Services/                     IReservationService, ReservationService (ingest rules + logging)
  Data/                         SqlQueries, IDbConnectionFactory, SqliteConnectionFactory,
                                DatabaseInitializer, IReservationRepository, ReservationRepository (Dapper)
  Infrastructure/               ProblemResults, ApiBehaviorConfiguration (binding-error handling + logging)
tests/Mize.Tests/
  Unit/                         one test class per production class
  Api/                          endpoint tests over real HTTP (in-memory repository)
  Integration/                  full-flow tests with the real SQLite database
  Helpers/                      fake clock, log capture, temp database, WebApplicationFactory wrapper
```

Request flow for `POST /ingest`: model binding → `ReservationRequestValidator` (400) → `IRateLimiter` (429) → `ReservationService` (insert / unchanged / stale / update) → `ISupplierStatsTracker` → response.

## Logging

All classes depend only on `ILogger<T>`. The one place that names a provider is `Program.cs`
(`ClearProviders()` + `AddJsonConsole()`); replacing it (for example with Serilog) is a change to those lines plus the package reference.

| Situation | Level |
|-----------|-------|
| Reservation created / updated | Information |
| Exact duplicate (unchanged) | Information |
| **Stale (out-of-order) update** | **Warning** |
| Throttled (429) | Warning |
| Validation failure / unbindable payload (400) | Warning |
| Unexpected exception / database failure (500) | Error |

Structured properties (`SupplierId`, `ReservationId`, …) are included in each entry. The levels are asserted by unit and API tests.

## Configuration

`appsettings.json` (override with environment variables, `__` as separator):

| Key | Default | Purpose |
|-----|---------|---------|
| `ConnectionStrings:Reservations` | `Data Source=data/mize.db` (Docker: `/data/mize.db`) | SQLite file location. |
| `RateLimit:MaxRequests` | `100` | Accepted requests per supplier per window. |
| `RateLimit:WindowSeconds` | `60` | Rolling window length. |

## Database

SQLite, accessed with Dapper. `docker-compose.yml` mounts the host folder `./data` into the container at `/data`, so the
file `./data/mize.db` persists across container restarts. The folder is git-ignored. The schema is created automatically at startup.

```sql
CREATE TABLE IF NOT EXISTS Reservations (
    SupplierId TEXT NOT NULL, ReservationId TEXT NOT NULL, RoomId TEXT NOT NULL,
    CheckIn TEXT NOT NULL, CheckOut TEXT NOT NULL, Price TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL,
    PRIMARY KEY (SupplierId, ReservationId)
);
```

All SQL lives in `src/Mize.Api/Data/SqlQueries.cs`, and every statement uses parameters.

## Dependencies

Managed with NuGet through the `dotnet` CLI (run inside the Docker SDK image, so nothing is installed locally).
Versions are pinned exactly in the `.csproj` files; use stable releases only.

| Package | Project | Purpose |
|---------|---------|---------|
| Dapper 2.1.35 | Api | Micro-ORM for SQL access |
| Microsoft.Data.Sqlite 8.0.11 | Api | SQLite ADO.NET provider |
| xunit 2.9.2, xunit.runner.visualstudio 2.8.2, Microsoft.NET.Test.Sdk 17.11.1 | Tests | Test framework and runner |
| Moq 4.20.72 | Tests | Mocks for unit tests |
| Microsoft.AspNetCore.Mvc.Testing 8.0.11 | Tests | In-memory API host (`WebApplicationFactory`) |

To add one: `docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:8.0 dotnet add src/Mize.Api package <Name> --version <x.y.z>`

## Tests

98 tests, all passing. Run with `docker compose run --rm tests`.

* **Unit** (`Unit/`): validator, rate limiter (fake clock, concurrency), stats tracker, service (mocked repository, log levels, write gate), repository and initializer (real temp SQLite file), connection factory, controller (mocked collaborators), `Reservation`, `ProblemResults` and `ApiBehaviorConfiguration`.
* **API** (`Api/`): every response of both endpoints over real HTTP — 201/200 success variants, 400 (business rules, missing property, malformed JSON, wrong type, empty body), 415, 405, 404, 429 with `Retry-After`, 500, plus log-level assertions.
* **Integration** (`Integration/`): full flow against a real SQLite file — create → duplicate → update → stale with database checks at each step, the real 100-request limit and window clearing, parallel duplicates, per-supplier isolation, and data surviving a restart.

## Known limitations

* Rate-limit and stats state is in memory: lost on restart and not shared across multiple API instances (the SQLite write gate is likewise per process).
* Rate-limit entries per supplier are kept for the process lifetime (an empty queue per supplier id seen).
* No authentication: `supplierId` comes from the request body and is trusted.

## Optional future work

* **Swagger / OpenAPI UI** (skipped for now). Add `Swashbuckle.AspNetCore` (pinned version), then in `Program.cs`:
  `builder.Services.AddEndpointsApiExplorer(); builder.Services.AddSwaggerGen();` and, inside an
  `if (app.Environment.IsDevelopment())` block, `app.UseSwagger(); app.UseSwaggerUI();`. Enabling `GenerateDocumentationFile`
  in the `.csproj` and passing the XML file to `AddSwaggerGen` would surface the existing `///` comments in the UI.
* Persist rate-limit state and stats (SQLite or Redis) to survive restarts and support multiple instances.
* Swap the logging provider (for example Serilog) — see [Logging](#logging).
