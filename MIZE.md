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
docker-compose up --build                        # API on http://localhost:8080, database in ./data/mize.db
./scripts/run-tests.sh                           # unit + API + integration tests inside the .NET SDK image
./scripts/run-tests.sh --all --coverage          # everything: coverage report + end-to-end tests (see Tests section)
```

(Use `docker compose` instead of `docker-compose` if your Docker has the Compose plugin.)

### Manual testing: `docker-compose.local.yml`

A second compose file for trying the API by hand. It is independent of the main one (own project name, own port, own data folder):

```bash
docker-compose -f docker-compose.local.yml up --build    # API on http://localhost:5080, database in ./data-local/mize.db
docker-compose -f docker-compose.local.yml down          # stop; delete ./data-local to start fresh
```

To look inside the database while you test, the same file has a stand-alone `sqlite3` shell container (opt-in `tools` profile, read-only by default; remove `-readonly` in the compose file to edit rows):

```bash
docker-compose -f docker-compose.local.yml run --rm sqlite3                  # interactive sqlite3 shell on ./data-local/mize.db
docker-compose -f docker-compose.local.yml run --rm sqlite3 -readonly /data/mize.db "SELECT * FROM Reservations;"
```

It overrides the rate limit through the environment variable `RateLimit__MaxRequests=5` (no code or `appsettings.json` change), so the
6th request within 60 seconds returns 429. Remove that line to get the real limit of 100.

Try it (use port 5080 with the local file):

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
docker-compose.local.yml        manual-testing setup: port 5080, ./data-local, rate limit 5
docker-compose.e2e.yml          end-to-end test setup: the real image + a test-runner container + sqlite3-verify
scripts/                        optional helpers: ingest.sh (manual request), run-tests.sh (wrapper around the test commands)
.config/dotnet-tools.json       pins ReportGenerator (coverage report) as a .NET local tool
MIZE.md                         this file
src/                            configuration and build files live here; code lives in src/Mize.Api/
  Mize.Api.csproj               project file (dependencies pinned here)
  appsettings.json              runtime configuration
  Directory.Build.props         moves bin/ and obj/ to src/bin and src/obj
  Mize.Api/                     code only
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
e2e/                            end-to-end tests (own folder so its obj/ and sources never mix with tests/)
  Mize.E2ETests.csproj          project file (not part of Mize.sln, so the default test run never needs a running API)
  Directory.Build.props         moves bin/ and obj/ to e2e/bin and e2e/obj
  Mize.E2ETests/                code only: E2EFixture, Payload, IngestFlowE2ETests, ThrottlingE2ETests
tests/
  Mize.Tests.csproj             project file
  coverage.runsettings          Coverlet settings (measures Mize.Api only)
  Directory.Build.props         moves bin/ and obj/ to tests/bin and tests/obj
  Mize.Tests/                   code only
    Unit/                         one test class per production class
    Api/                          endpoint tests over real HTTP (in-memory repository)
    Integration/                  full-flow tests with the real SQLite database
    Helpers/                      fake clock, log capture, temp database, WebApplicationFactory wrapper
```

Build output lives in `src/bin`, `src/obj`, `tests/bin` and `tests/obj` (git-ignored). The `docker-compose` tests service keeps its
NuGet package cache in `./.nuget` (git-ignored, via `NUGET_PACKAGES`) so nothing is stored outside the repo.

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

`src/appsettings.json` (override with environment variables, `__` as separator):

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
Versions are pinned exactly in the `src/Mize.Api.csproj` and `tests/Mize.Tests.csproj`; use stable releases only.

| Package | Project | Purpose |
|---------|---------|---------|
| Dapper 2.1.35 | Api | Micro-ORM for SQL access |
| Microsoft.Data.Sqlite 8.0.11 | Api | SQLite ADO.NET provider |
| xunit 2.9.2, xunit.runner.visualstudio 2.8.2, Microsoft.NET.Test.Sdk 17.11.1 | Tests | Test framework and runner |
| Moq 4.20.72 | Tests | Mocks for unit tests |
| coverlet.collector 6.0.2 | Tests | Code-coverage collector (only active with the coverage option) |
| xunit, xunit.runner.visualstudio, Microsoft.Test.Sdk, Microsoft.Data.Sqlite | E2E | E2E framework; SQLite only to read the file the API wrote |
| Microsoft.AspNetCore.Mvc.Testing 8.0.11 | Tests | In-memory API host (`WebApplicationFactory`) |

To add one: `docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:8.0 dotnet add src/Mize.Api.csproj package <Name> --version <x.y.z>`

## Tests

Nothing depends on the scripts: every kind of test runs with standard commands, and `scripts/run-tests.sh` is only an optional
convenience wrapper around them. Docker is the only requirement; with a local .NET 8 SDK the `dotnet` commands work directly too.

| What | Standard command (Docker) | Directly with a .NET SDK |
|------|---------------------------|--------------------------|
| Unit + API + integration (98 tests) | `docker-compose --profile tests run --rm tests` | `dotnet test Mize.sln` |
| Same, with coverage report | `docker-compose --profile coverage run --rm coverage` | `dotnet test Mize.sln --settings tests/coverage.runsettings --collect:"XPlat Code Coverage" --results-directory coverage/raw`, then `dotnet tool restore`, then `dotnet reportgenerator "-reports:coverage/raw/**/coverage.cobertura.xml" -targetdir:coverage/report -reporttypes:"Html;TextSummary"` |
| End-to-end (13 tests, about 1 minute) | `docker-compose -f docker-compose.e2e.yml up --build --abort-on-container-exit --exit-code-from e2e-tests`, then optionally `docker-compose -f docker-compose.e2e.yml --profile verify run --rm sqlite3-verify`, then `docker-compose -f docker-compose.e2e.yml down` | start the API (`docker-compose up --build`), then `E2E_BASE_URL=http://localhost:8080 E2E_DATABASE_PATH=./data/mize.db dotnet test e2e/Mize.E2ETests.csproj` |

(Use `docker compose` instead of `docker-compose` if your Docker has the Compose plugin.) The optional wrapper adds flags on top:

| Command | Runs | Takes |
|---------|------|-------|
| `./scripts/run-tests.sh` | unit + API + integration (98 tests) | a few seconds |
| `./scripts/run-tests.sh --coverage` | the same, plus a coverage report | a few seconds more |
| `./scripts/run-tests.sh --e2e` | only the end-to-end tests (13 tests) plus the sqlite3 database check; cleans up `./data-e2e` afterwards | about 1 minute |
| `./scripts/run-tests.sh --all` | default suite, then end-to-end | about 1 minute |
| `./scripts/run-tests.sh --all --coverage` | coverage for the default suite, then end-to-end | about 1 minute |

The script exits non-zero if anything requested fails. Every run lists each test with its result and duration.

| Kind | Tests | What is real | What is faked |
|------|-------|--------------|---------------|
| Unit (`tests/.../Unit`) | 63 | the class under test | its collaborators, the clock |
| API (`tests/.../Api`) | 27 | routing, binding, validation, limiter, service, stats, JSON, status codes | repository (in-memory), clock |
| Integration (`tests/.../Integration`) | 8 | everything incl. Dapper and a real SQLite file, in-process | only the clock |
| End-to-end (`e2e/`) | 13 | the real Docker image, a real network, the mounted SQLite file, the production settings and the real clock | nothing |

* **Unit:** validator, rate limiter (fake clock, concurrency), stats tracker, service (mocked repository, log levels, write gate), repository and initializer (real temp SQLite file), connection factory, controller (mocked collaborators), `Reservation`, `ProblemResults` and `ApiBehaviorConfiguration`.
* **API:** every response of both endpoints over HTTP: 201/200 success variants, 400 (business rules, missing property, malformed JSON, wrong type, empty body), 415, 405, 404, 429 with `Retry-After`, 500, plus log-level assertions.
* **Integration:** the full flow against a real SQLite file: create, duplicate, update, stale with database checks at each step, the real 100-request limit and window clearing (fake clock), parallel duplicates, per-supplier isolation, and data surviving a restart.
* **End-to-end:** `docker-compose.e2e.yml` builds the production image and starts it (own project name, own `./data-e2e` folder), then a test-runner container calls it over the compose network. Tests verify what was really persisted by reading the SQLite file (read-only) through the shared volume. The throttling test really sends 100 + 1 requests and waits for the true 60-second window to clear (hence the one minute). Each test uses a fresh supplier id so tests never share rate-limit windows. The E2E project is deliberately **not** in `Mize.sln`, which is how "with or without E2E" works. The test host console logs are silenced in API/integration tests; logs are captured in memory and asserted on instead.
* **Independent database check (`sqlite3-verify`):** after the E2E tests, a second container with the stand-alone `sqlite3` program (image `keinos/sqlite3`, pinned to 3.54.1; sqlite.org publishes no Docker image, so this is a widely used community one) opens the file the API wrote, read-only. It checks `PRAGMA integrity_check`, that rows exist, that the primary key is `(SupplierId, ReservationId)`, that no row has a negative price or `checkOut <= checkIn`, and that every timestamp is stored as UTC. This proves the file is a valid, portable SQLite database for a different SQLite build than the one bundled in .NET. It is a separate step because `docker-compose up --abort-on-container-exit` stops all containers the moment the test runner exits; `run-tests.sh --e2e` chains the two. It was also checked against a deliberately bad database to confirm it fails.
* **What E2E does not cover:** restarting the container (the test runner cannot control Docker). "Data survives a restart" is covered by an integration test.

### Code coverage

The coverage command above uses **Coverlet** (`coverlet.collector`, the .NET counterpart of JaCoCo) and **ReportGenerator** (a .NET local tool pinned in `.config/dotnet-tools.json`, restored with `dotnet tool restore`). It covers the unit, API and integration tests (never E2E) and measures only the `Mize.Api` assembly (`tests/coverage.runsettings`). Output: a text summary in the console and an HTML report at `coverage/report/index.html` (git-ignored). No minimum threshold is enforced. Current result: 98.5% line, 91.4% branch, 100% method coverage. The few uncovered lines are the failure-cleanup branch of `SqliteConnectionFactory` and an unreachable `default` branch in the controller. Coverage shows which lines ran, not whether the assertions are good.

The coverage service sets `VSTEST_DISABLE_ARTIFACTS_DIRECTORY_VERIFICATION=1` because VSTest's temp-folder permission self-check fails inside the container (it only guards a temp directory).

### Manual request script

`./scripts/ingest.sh` sends one reservation with `curl` (body kept in a variable at the top) and prints the supplier's stats. It targets the local compose API on port 5080 by default (`BASE_URL` overrides).

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
