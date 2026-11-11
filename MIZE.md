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

### Why everything runs in Docker (and why there are so many `docker-compose` commands)

**Nothing has to be installed on your machine except Docker.** No .NET SDK, no SQLite, no coverage tools. That is the
whole reason `docker-compose` is used for so much here: building, running the API, running every kind of test, producing
the coverage report and even looking inside the database all happen inside containers, so your machine stays clean and
every run behaves the same everywhere. Every compose file below exists for exactly that, and the scripts in `scripts/`
are only shortcuts around the same commands.

```bash
./scripts/run-api.sh               # start the API "like production" (docker-compose.yaml) -> http://localhost:8080
./scripts/ingest.sh                # mini simulator: help (then: reserve / stats)
./scripts/run-tests.sh             # unit + API + integration tests, with coverage (docker-compose.tests.yaml)
./scripts/run-tests.sh --all       # the same, then the end-to-end tests (docker-compose.e2e.yaml)
```

(Use `docker compose` instead of `docker-compose` if your Docker has the Compose plugin. The scripts detect which one you have.)

### The three compose files

There are exactly three, each with one purpose:

| File | Services | Purpose |
|------|----------|---------|
| `docker-compose.yaml` | `api`, `sqlite3` (on demand) | Runs the API **like production**: the real image, production settings (rate limit 100 per 60 s), `restart: unless-stopped`, database in `./data`. The `sqlite3` service is a read-only shell on that database, so SQLite never has to be installed. |
| `docker-compose.tests.yaml` | `tests` (only one) | Runs all tests except end-to-end (unit + API + integration) with coverage, then prints the coverage summary and writes the HTML report. |
| `docker-compose.e2e.yaml` | `api`, `e2e-tests`, `sqlite3-verify` | End-to-end tests against the real image, plus the independent `sqlite3` database check. |

### Running the API and trying it by hand

```bash
docker-compose up --build -d       # or ./scripts/run-api.sh (also waits until the API answers and prints next steps)
docker-compose down                # stop it; the data in ./data stays
```

`./scripts/ingest.sh` is a small simulator (it uses `curl`; the request body is a variable inside the script). Without a command it prints its help:

| Command | What it does |
|---------|--------------|
| `./scripts/ingest.sh` | prints the help |
| `./scripts/ingest.sh reserve [options]` | sends a reservation (`POST /api/reservations/ingest`). Options change any field: `--supplier`, `--id`, `--room`, `--check-in`, `--check-out`, `--price`, `--updated-at`. `--count N` sends N new reservations in a row and prints a summary of the HTTP status codes. |
| `./scripts/ingest.sh stats [supplierId]` | shows ingested vs throttled counts (`GET /api/reservations/stats/{supplierId}`) |

```bash
./scripts/ingest.sh reserve                                  # 201 created
./scripts/ingest.sh reserve                                  # 200 unchanged: same details, nothing written
./scripts/ingest.sh reserve --price 500 --updated-at 2026-07-02T09:00:00Z   # 200 updated
./scripts/ingest.sh reserve --updated-at 2026-06-01T00:00:00Z               # 200 updated: an older timestamp is still applied (logged as a WARN)
./scripts/ingest.sh reserve --price -5                       # 400 validation error
./scripts/ingest.sh reserve --supplier flood --count 105     # 100 x 201, then 429: the real limit is 100 per 60 s
./scripts/ingest.sh stats flood                              # ingested 100, throttled 5
```

To look inside the database (SQLite is not installed on your machine; the `sqlite3` service runs it in Docker, read-only by default; remove `-readonly` in the compose file to edit rows):

```bash
docker-compose run --rm sqlite3                              # interactive sqlite3 shell on ./data/mize.db
docker-compose run --rm sqlite3 -readonly /data/mize.db "SELECT * FROM Reservations;"
```

Plain `curl` works too:

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
| 200 | `updated` | Same id, different details: record updated (whatever the `updatedAtUtc` is; an older one is logged at WARN). |
| 200 | `unchanged` | Identical to what is stored: **nothing written**; message says "nothing changed". |
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
  "breakdown": { "created": 98, "updated": 0, "unchanged": 1 }
}
```

* `ingested` = `created + updated + unchanged` (accepted requests that were ingested).
* `throttled` = requests rejected with 429.
* An unknown supplier returns **200 with zeros** (not 404). The supplier id is case-sensitive.

## Behaviour rules

* **Identity:** a reservation is unique per `(supplierId, reservationId)`; two suppliers may reuse an id.
* **Exact duplicate:** all of room, check-in, check-out, price and `updatedAtUtc` equal → nothing is written (`unchanged`).
* **Changed details:** the spec says the existing record is updated, so any difference updates it in place (`updated`), whatever the `updatedAtUtc` is.
* **Out-of-order timestamp:** an update whose `updatedAtUtc` is older than the stored one is **still applied** (the spec has no exception to "different details → update") but is logged at **Warning**, because it usually means a late or replayed message.
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
| Older `updatedAtUtc` | Applied like any other update, logged at Warning | The specification says different details always update; no special case and no way for a bad timestamp to block a record. |
| Identity & stats | Per `(supplierId, reservationId)`; ingested = created+updated+unchanged | Avoids cross-supplier collisions. |
| Validation | Strict | Bad data never reaches the database. |
| Solution layout | One API project + one test project | Compact; right-sized for this scope. |
| Logging | Built-in `ILogger` with JSON console | No extra dependency (stability); swappable, see [Logging](#logging). |
| Assertions | Plain xUnit `Assert` | No extra package or license concern. |
| Swagger | Skipped for now | See [Optional future work](#optional-future-work). |
| Build/test tooling | Docker only (`docker-compose`) | Nothing to install on the developer machine: no .NET SDK, no SQLite. |

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
docker-compose.yaml             the API run like production (restart policy, ./data mount) + on-demand sqlite3 shell
docker-compose.tests.yaml       one service, `tests`: unit + API + integration tests with coverage
docker-compose.e2e.yaml         end-to-end tests: the real image + a test-runner container + sqlite3-verify
scripts/                        optional shortcuts: run-api.sh (start the API), ingest.sh (mini simulator), run-tests.sh (all test modes)
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

Build output lives in `src/bin`, `src/obj`, `tests/bin` and `tests/obj` (git-ignored). The `tests` service keeps its
NuGet package cache in `./.nuget` (git-ignored, via `NUGET_PACKAGES`) so nothing is stored outside the repo.

Request flow for `POST /ingest`: model binding → `ReservationRequestValidator` (400) → `IRateLimiter` (429) → `ReservationService` (insert / unchanged / update) → `ISupplierStatsTracker` → response.

## Logging

All classes depend only on `ILogger<T>`. The one place that names a provider is `Program.cs`
(`ClearProviders()` + `AddJsonConsole()`); replacing it (for example with Serilog) is a change to those lines plus the package reference.

| Situation | Level |
|-----------|-------|
| Reservation created / updated | Information |
| Exact duplicate (unchanged) | Information |
| **Out-of-order update** (older `updatedAtUtc`, still applied) | **Warning** |
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

SQLite, accessed with Dapper. `docker-compose.yaml` mounts the host folder `./data` into the container at `/data`, so the
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
| coverlet.collector 6.0.2 | Tests | Code-coverage collector (active on every test run through `RunSettingsFilePath`) |
| xunit, xunit.runner.visualstudio, Microsoft.Test.Sdk, Microsoft.Data.Sqlite | E2E | E2E framework; SQLite only to read the file the API wrote |
| Microsoft.AspNetCore.Mvc.Testing 8.0.11 | Tests | In-memory API host (`WebApplicationFactory`) |

To add one: `docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:8.0 dotnet add src/Mize.Api.csproj package <Name> --version <x.y.z>`

## Tests

Nothing depends on the scripts: every kind of test runs with standard commands, and `scripts/run-tests.sh` is only an optional
convenience wrapper around them. Docker is the only requirement; with a local .NET 8 SDK the `dotnet` commands work directly too.

| What | Standard command (Docker) | Directly with a .NET SDK |
|------|---------------------------|--------------------------|
| Unit + API + integration (98 tests), **always with coverage** | `docker-compose -f docker-compose.tests.yaml run --rm tests` (also prints the coverage summary and writes the HTML report) | `dotnet test Mize.sln` (collects coverage into `tests/TestResults`; add `-p:RunSettingsFilePath=` to run without it) |
| Coverage report from a plain `dotnet test` | – | `dotnet tool restore`, then `dotnet reportgenerator "-reports:tests/TestResults/**/coverage.cobertura.xml" -targetdir:coverage/report -reporttypes:"Html;TextSummary"` |
| End-to-end (13 tests, about 1 minute) | `docker-compose -f docker-compose.e2e.yaml up --build --abort-on-container-exit --exit-code-from e2e-tests`, then optionally `docker-compose -f docker-compose.e2e.yaml --profile verify run --rm sqlite3-verify`, then `docker-compose -f docker-compose.e2e.yaml down` | start the API (`docker-compose up --build`), then `E2E_BASE_URL=http://localhost:8080 E2E_DATABASE_PATH=./data/mize.db dotnet test e2e/Mize.E2ETests.csproj` |

(Use `docker compose` instead of `docker-compose` if your Docker has the Compose plugin.) The optional wrapper adds flags on top:

| Command | Runs | Takes |
|---------|------|-------|
| `./scripts/run-tests.sh` | unit + API + integration (98 tests), with the coverage summary and HTML report | about 15 seconds |
| `./scripts/run-tests.sh --e2e` | only the end-to-end tests (13 tests) plus the sqlite3 database check; cleans up `./data-e2e` afterwards | about 1 minute |
| `./scripts/run-tests.sh --all` | default suite, then end-to-end plus the sqlite3 check | about 1.5 minutes |

The script exits non-zero if anything requested fails. Every run lists each test with its result and duration.

| Kind | Tests | What is real | What is faked |
|------|-------|--------------|---------------|
| Unit (`tests/.../Unit`) | 63 | the class under test | its collaborators, the clock |
| API (`tests/.../Api`) | 27 | routing, binding, validation, limiter, service, stats, JSON, status codes | repository (in-memory), clock |
| Integration (`tests/.../Integration`) | 8 | everything incl. Dapper and a real SQLite file, in-process | only the clock |
| End-to-end (`e2e/`) | 13 | the real Docker image, a real network, the mounted SQLite file, the production settings and the real clock | nothing |

* **Unit:** validator, rate limiter (fake clock, concurrency), stats tracker, service (mocked repository, log levels, write gate), repository and initializer (real temp SQLite file), connection factory, controller (mocked collaborators), `Reservation`, `ProblemResults` and `ApiBehaviorConfiguration`.
* **API:** every response of both endpoints over HTTP: 201/200 success variants, 400 (business rules, missing property, malformed JSON, wrong type, empty body), 415, 405, 404, 429 with `Retry-After`, 500, plus log-level assertions.
* **Integration:** the full flow against a real SQLite file: create, duplicate, update, older timestamp (still updated) with database checks at each step, the real 100-request limit and window clearing (fake clock), parallel duplicates, per-supplier isolation, and data surviving a restart.
* **End-to-end:** `docker-compose.e2e.yaml` builds the production image and starts it (own project name, own `./data-e2e` folder), then a test-runner container calls it over the compose network. Tests verify what was really persisted by reading the SQLite file (read-only) through the shared volume. The throttling test really sends 100 + 1 requests and waits for the true 60-second window to clear (hence the one minute). Each test uses a fresh supplier id so tests never share rate-limit windows. The E2E project is deliberately **not** in `Mize.sln`, which is how "with or without E2E" works. The test host console logs are silenced in API/integration tests; logs are captured in memory and asserted on instead.
* **Independent database check (`sqlite3-verify`):** after the E2E tests, a second container with the stand-alone `sqlite3` program (image `keinos/sqlite3`, pinned to 3.54.1; sqlite.org publishes no Docker image, so this is a widely used community one) opens the file the API wrote, read-only. It checks `PRAGMA integrity_check`, that rows exist, that the primary key is `(SupplierId, ReservationId)`, that no row has a negative price or `checkOut <= checkIn`, and that every timestamp is stored as UTC. This proves the file is a valid, portable SQLite database for a different SQLite build than the one bundled in .NET. It is a separate step because `docker-compose up --abort-on-container-exit` stops all containers the moment the test runner exits; `run-tests.sh --e2e` chains the two. It was also checked against a deliberately bad database to confirm it fails.
* **What E2E does not cover:** restarting the container (the test runner cannot control Docker). "Data survives a restart" is covered by an integration test.

### Code coverage

Coverage is **always on**: `tests/Mize.Tests.csproj` sets `RunSettingsFilePath` to `tests/coverage.runsettings`, which enables the **Coverlet** collector (`coverlet.collector`, the .NET counterpart of JaCoCo). So every `dotnet test` of that project, with or without Docker and without any extra flag, writes a Cobertura XML file (to `tests/TestResults/`, git-ignored). It costs about half a second per run; pass `-p:RunSettingsFilePath=` to run once without it. It covers the unit, API and integration tests (the E2E project is separate and never included) and measures only the `Mize.Api` assembly.

The compose `tests` service goes one step further: after the tests it runs **ReportGenerator** (a .NET local tool pinned in `.config/dotnet-tools.json`, restored with `dotnet tool restore`) to print a text summary and write an HTML report to `coverage/report/index.html` (git-ignored). No minimum threshold is enforced. Current result: 98.5% line, 91.4% branch, 100% method coverage. The few uncovered lines are the failure-cleanup branch of `SqliteConnectionFactory` and an unreachable `default` branch in the controller. Coverage shows which lines ran, not whether the assertions are good.

The `tests` service sets `VSTEST_DISABLE_ARTIFACTS_DIRECTORY_VERIFICATION=1` because VSTest's temp-folder permission self-check fails inside the container (it only guards a temp directory).

### Manual testing scripts

`./scripts/run-api.sh` starts the API like production and `./scripts/ingest.sh` is the mini simulator (see Quick start). They are conveniences only: the compose files and `curl` do the same.

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
