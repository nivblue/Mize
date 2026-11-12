# MIZE — Reservation Ingest API

An ASP.NET Core (.NET 8) Web API that ingests supplier reservations into SQLite (via Dapper), de-duplicates them,
applies a per-supplier rolling rate limit, and reports ingest/throttle statistics.

## Contents
1. [Quick start](#quick-start)
2. [Endpoints](#endpoints)
3. [Behaviour rules](#behaviour-rules)
4. [Design decisions](#design-decisions)
5. [Statistics: SQL as the source of truth, memory as a cache](#statistics-sql-is-the-source-of-truth-memory-is-a-cache)
6. [Project layout](#project-layout)
7. [Logging](#logging)
8. [Configuration](#configuration)
9. [Database](#database)
10. [Dependencies](#dependencies)
11. [Tests](#tests)
12. [Known limitations](#known-limitations)
13. [Optional future work](#optional-future-work)

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

* `ingested` = the number of **reservations stored** for the supplier, read from SQL. An exact duplicate is ignored and an update changes an existing reservation, so **neither adds to `ingested`**: one reservation sent five times is one ingested reservation.
* `throttled` = requests rejected with 429.
* `breakdown` = how the supplier's accepted requests ended: `created` (new reservation), `updated` (changed details) and `unchanged` (exact duplicates, nothing written).
* All of these survive a restart (see [Statistics](#statistics-sql-is-the-source-of-truth-memory-is-a-cache)); after a crash the request counters can miss the last few seconds, `ingested` is always exact.
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
* **Statistics:** `ingested` counts reservations (rows), never requests; duplicates are ignored for it. The request counters (`created`, `updated`, `unchanged`, `throttled`) are cached in memory and saved to SQL every 5 seconds and on graceful shutdown.
* **Validation (strict):** `supplierId`, `reservationId`, `roomId` non-empty; `checkIn`, `checkOut`, `updatedAtUtc` have offset `+00:00`/`Z`; `checkOut` after `checkIn`; `price >= 0`. Missing JSON properties, malformed JSON and wrong types are also 400.

## Design decisions

Each choice below was made together with the project owner.

| Decision | Choice | Why |
|----------|--------|-----|
| Rate-limit state | In-memory, a clean window after every restart | The limiter's state is a short-lived quota ledger, not queued work: a REST API has no waiting messages to recover, and a limiter exists to reduce pressure, so starting clean is harmless. Worst case: one extra burst of up to 100 requests per supplier per restart. Several instances would each have their own window. |
| Statistics state | SQL is the truth, memory is a cache (see below) | `ingested` is read from the rows so it is exact and restart-proof; request counters are cached and flushed in batches. |
| Throttled requests and the window | Rejected requests don't count; 429 + `Retry-After` | Supplier recovers as soon as the window clears; standard HTTP semantics. |
| Response codes | 201 / 200 / 200 with a `status` field | Makes "nothing changed" explicit in the body. |
| Older `updatedAtUtc` | Applied like any other update, logged at Warning | The specification says different details always update; no special case and no way for a bad timestamp to block a record. |
| Identity | Per `(supplierId, reservationId)` | Avoids cross-supplier collisions. |
| Meaning of `ingested` | Number of reservations stored (rows); duplicates and updates do not add | Matches the spec's wording ("reservations ingested" vs "requests throttled") and the rule that duplicates are ignored. Earlier it counted accepted requests, which counted duplicates too. |
| Stats response shape | Unchanged: `ingested`, `throttled`, `breakdown` | Only the meaning of `ingested` changed. |
| Validation | Strict | Bad data never reaches the database. |
| Solution layout | One API project + one test project | Compact; right-sized for this scope. |
| Logging | Built-in `ILogger` with JSON console | No extra dependency (stability); swappable, see [Logging](#logging). |
| Assertions | Plain xUnit `Assert` | No extra package or license concern. |
| Swagger | Skipped for now | See [Optional future work](#optional-future-work). |
| Build/test tooling | Docker only (`docker-compose`) | Nothing to install on the developer machine: no .NET SDK, no SQLite. |

Implementation choices made along the way:

* **Controllers** (not Minimal APIs): clean separation and straightforward unit testing of actions.
* **`TimeProvider`** (built into .NET 8) for the clock, so the rolling window is tested without sleeping.
* **Write gate:** one shared `IWriteGate` (a `SemaphoreSlim`) serialises read-decide-write in `ReservationService`, so concurrent duplicates cannot both insert (which would violate the primary key). The statistics cache takes the same gate for its first load of a supplier. Adequate for one process on one SQLite file.
* **SQLite storage types:** dates are stored as ISO-8601 UTC text and price as invariant decimal text, because SQLite has no datetime/decimal type; this round-trips values exactly.
* **Journal mode:** SQLite's default (not WAL), because WAL is unreliable on Docker bind mounts from macOS.
* **Errors:** `UseExceptionHandler` + `AddProblemDetails` turn unhandled exceptions into 500 problem responses (and log them at Error).

## Statistics: SQL is the source of truth, memory is a cache

This section records the decisions behind `GET /stats` and their trade-offs.

### Where each number comes from

```
 ingested              = number of rows in Reservations for the supplier      (SQL, always exact, survives anything)
 created/updated/      = request counters, saved in the SupplierStats table   (cached in memory, flushed in batches)
 unchanged/throttled
```

The row count is never stored as a counter. SQL has it, so it can never drift; only what SQL cannot know (how many requests were
duplicates, updates or throttled: they leave no row) needs its own counters.

### How the cache works

```
 GET stats(acme)
   ├─ acme cached?  yes → answer from memory (no database, microseconds)
   └─ no (first read since the process started) → ONE SQL statement: row count + saved counters
                                                  → cache it → answer
 ingest → created (after the insert has COMMITTED, inside the write gate)  → cache: ingested +1, created +1
 ingest → updated / duplicate                                              → cache: updated +1 / unchanged +1 (ingested unchanged)
 request throttled                                                         → cache: throttled +1
 every Stats:FlushIntervalSeconds (default 5) and on graceful shutdown     → ONE transaction adds the unsaved increments
                                                                             of the suppliers that have any to SupplierStats
```

Recording never touches the database, so the request path is as fast as before. The database is read once per supplier and
written at most once per flush interval, however large a flood is.

### The strict rules that keep the cached numbers exact

They hold for one instance that is the only writer of the database file.

| # | Rule | Why | Protected by |
|---|------|-----|--------------|
| 1 | The first load of a supplier happens while holding the write gate | No ingest can be half-done during the load, so a row is never missed or counted twice | unit: `Get_OnFirstLoad_WaitsForTheWriteGate`, `FirstLoad_RacingAnIngest_CountsTheNewRowExactlyOnce`; integration: concurrent ingests vs reads equal SQL |
| 2 | The outcome is recorded right after the commit, inside the gate, with no `await` between | The count is never ahead of the database | unit: `Ingest_Created_IsRecordedAfterTheInsert_InsideTheGate` |
| 3 | If an insert throws (it might have committed), the supplier's cached row count is dropped and reloaded | A commit followed by a failure would otherwise leave the count one too low forever | unit: `Ingest_InsertThrows_InvalidatesTheRowCount_...`; integration: `AmbiguousInsertFailure_DoesNotLeaveAStaleRowCount` |
| 4 | Only `created` increments the row count, and only while it is loaded | Duplicates and updates are not new reservations | unit: `RecordOutcome_UpdatedAndUnchanged_DoNotChangeIngested` |
| 5 | A read of a supplier without data creates no cache entry | Random ids in `GET` cannot grow memory | unit: `Get_UnknownSupplier_ReturnsZeros_AndIsNotCached`; API: `Get_UnknownSupplier_IsNotCached` |
| 6 | Flush adds deltas (never overwrites), and a flush and a load never overlap | Totals are exact, repeated or concurrent flushes cannot double count, and what a reader sees never goes backwards | unit: the concurrency test with writers + flusher + reader; `Flush_*` tests |

Every rule was verified by **deliberately breaking it** and confirming that tests fail (rules 1, 2, 3 and 6, plus a "duplicates count as
ingested" bug). The one mutation that no test noticed, removing the "only while loaded" guard of rule 4, is an *equivalent* mutant: the load
assigns the row count, so an early increment is always overwritten. The guard documents intent.

### Flushing and graceful shutdown

`StatsFlushService` (a background service) flushes on a timer and once more when the application stops. Hosted services stop in reverse
order of registration and the web server is registered last, so by the time the final flush runs the server has finished its requests.
A failed flush is logged at Error and **nothing is lost**: the increments stay in memory and the next flush retries them. This was verified
three ways: unit and integration tests (dispose the host, then a new host sees the totals); the E2E check (`sqlite3-verify` confirms that
every supplier's saved `created` counter equals its reservation rows after the container was stopped); and a manual real `docker stop`
with the periodic flush disabled, where the file had no `SupplierStats` rows before the stop and exactly the right ones after it.

### Costs and trade-offs

* **A hard crash** (kill -9, power loss, out of memory) loses the request counters of the last interval, 5 seconds by default. `ingested` is
  unaffected because it comes from the rows. A graceful stop loses nothing.
* **`breakdown.created` can lag the rows** after an ambiguous failure (rule 3): `ingested` is reloaded from SQL, the created counter was never
  incremented. `ingested` is the authoritative number.
* **The first read of each supplier waits for the write gate**, so it can wait behind one in-flight ingest (milliseconds), once per supplier
  per process lifetime. Cached reads never wait for the gate.
* **One cache entry per supplier is kept for the process lifetime** (no eviction), the same unbounded-growth property the rate limiter has; see [Known issue: unbounded memory per supplier id](#known-issue-unbounded-memory-per-supplier-id).
* **Single writer assumption:** a second API instance, or someone editing the database by hand, would make cached counts stale until a restart.
  The `sqlite3` shell container is read-only by default for this reason.
* **Cost of the SQL read** (measured on a throwaway database with 1,000,000 reservations, real schema, index-only scan): a supplier with
  100 reservations 0.04 ms, 1,000 reservations 0.06 ms, 100,000 reservations 2.4 ms. It happens once per supplier per process lifetime.
* **More parts:** one table, one background service, one cache class and a shared gate.

### Alternatives considered

| Alternative | Why it was not chosen |
|-------------|-----------------------|
| Run the count query on every `GET` | Exact and simple, but each read touches the database and, in SQLite's default journal mode, can briefly contend with ingest commits. The cache keeps reads off the database. |
| Load all suppliers' counters into memory at startup | Startup time grows with the data (a `GROUP BY` over 1,000,000 rows took 34 ms warm); loading per supplier on first read has no startup cost. |
| Write every throttled request / outcome to SQL (write-through) | Measured: 3,000 separate writes took 1.34 s of serialised write time (about 0.45 ms each) against under 10 ms for one batched flush. A flood of throttled requests would starve legitimate ingests of the write lock, which defeats the purpose of the limiter. |
| Do not recover anything (counters reset on restart) | Mixed lifetimes in one response (lifelong `ingested`, since-start counters) were confusing and `throttled` is the spec's own second number. |
| Persist the rate-limit window | See below. |

### The rate limiter is deliberately not persisted

It keeps a clean window after each restart. Reasons: (1) this is a REST API, not a queue, so there is no pending work to recover and a lost request
is simply retried by its client; (2) a limiter exists to reduce pressure and overload, so a service that has just started from a nearly clean state
does not need yesterday's quota ledger. The compromise is at most one extra burst per supplier per restart, and several instances multiply the limit.

### Scale path

If suppliers were ever routed to a fixed instance (a load balancer hashing by supplier), the cache, the limiter window and the counters would
all stay exact, because each supplier's state would live in one place. Things to know: the supplier id of a `POST` is in the JSON body, and a
load balancer routes on paths and headers, so an agreed header would be needed; SQLite does not share across hosts, so each instance would need
its own database for its suppliers, or a server database; and when a supplier moves it meets a cold cache and a fresh window, exactly like a restart.

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
    Controllers/ReservationsController.cs   the two endpoints (validate -> limit -> ingest; stats read)
    Models/                       ReservationRequest, Reservation, IngestStatus, IngestResponse, SupplierStats
    Validation/ReservationRequestValidator.cs
    RateLimiting/                 IRateLimiter, SlidingWindowRateLimiter, RateLimitOptions
    Stats/                        ISupplierStatsTracker, SupplierStatsCache (the cache), StatsCounters,
                                  IStatsFlusher, StatsFlushService (periodic + shutdown flush), StatsOptions
    Services/                     IReservationService, ReservationService (ingest rules + logging), IWriteGate, WriteGate
    Data/                         SqlQueries, IDbConnectionFactory, SqliteConnectionFactory, DatabaseInitializer,
                                  IReservationRepository, ReservationRepository, IStatsRepository, StatsRepository (Dapper)
    Infrastructure/               ProblemResults, ApiBehaviorConfiguration (binding-error handling + logging)
e2e/                            end-to-end tests (own folder so its obj/ and sources never mix with tests/)
  Mize.E2ETests.csproj          project file (not part of Mize.sln, so the default test run never needs a running API)
  Directory.Build.props         moves bin/ and obj/ to e2e/bin and e2e/obj
  Mize.E2ETests/                code only: E2EFixture (HTTP + read-only SQLite access), Payload, IngestFlowE2ETests, ThrottlingE2ETests
tests/
  Mize.Tests.csproj             project file
  coverage.runsettings          Coverlet settings (measures Mize.Api only)
  Directory.Build.props         moves bin/ and obj/ to tests/bin and tests/obj
  Mize.Tests/                   code only
    Unit/                         one test class per production class
    Api/                          endpoint tests over real HTTP (in-memory repository)
    Integration/                  full-flow tests with the real SQLite database
    Helpers/                      fake clock, log capture, temp database, fake stats repository, WebApplicationFactory wrapper
```

Build output lives in `src/bin`, `src/obj`, `tests/bin` and `tests/obj` (git-ignored). The `tests` service keeps its
NuGet package cache in `./.nuget` (git-ignored, via `NUGET_PACKAGES`) so nothing is stored outside the repo.

Request flow for `POST /ingest`: model binding → `ReservationRequestValidator` (400) → `IRateLimiter` (429) → `ReservationService` (insert / unchanged / update, records the outcome in the statistics inside the write gate) → response. `GET /stats` reads `SupplierStatsCache`.

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
| Statistics flush failed (retried) / final flush failed at shutdown | Error |

Structured properties (`SupplierId`, `ReservationId`, …) are included in each entry. The levels are asserted by unit and API tests.

## Configuration

`src/appsettings.json` (override with environment variables, `__` as separator):

| Key | Default | Purpose |
|-----|---------|---------|
| `ConnectionStrings:Reservations` | `Data Source=data/mize.db` (Docker: `/data/mize.db`) | SQLite file location. |
| `Stats:FlushIntervalSeconds` | `5` | How often the cached counters are saved to SQL (fractions allowed). They are also saved on graceful shutdown. |
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

-- saved request counters (the reservation row count is NOT stored: it is read from Reservations)
CREATE TABLE IF NOT EXISTS SupplierStats (
    SupplierId TEXT PRIMARY KEY,
    Created INTEGER NOT NULL DEFAULT 0, Updated INTEGER NOT NULL DEFAULT 0,
    Unchanged INTEGER NOT NULL DEFAULT 0, Throttled INTEGER NOT NULL DEFAULT 0
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
| Unit + API + integration (145 tests), **always with coverage** | `docker-compose -f docker-compose.tests.yaml run --rm tests` (also prints the coverage summary and writes the HTML report) | `dotnet test Mize.sln` (collects coverage into `tests/TestResults`; add `-p:RunSettingsFilePath=` to run without it) |
| Coverage report from a plain `dotnet test` | – | `dotnet tool restore`, then `dotnet reportgenerator "-reports:tests/TestResults/**/coverage.cobertura.xml" -targetdir:coverage/report -reporttypes:"Html;TextSummary"` |
| End-to-end (15 tests, about 1 minute) | `docker-compose -f docker-compose.e2e.yaml up --build --abort-on-container-exit --exit-code-from e2e-tests`, then optionally `docker-compose -f docker-compose.e2e.yaml --profile verify run --rm sqlite3-verify`, then `docker-compose -f docker-compose.e2e.yaml down` | start the API (`docker-compose up --build`), then `E2E_BASE_URL=http://localhost:8080 E2E_DATABASE_PATH=./data/mize.db dotnet test e2e/Mize.E2ETests.csproj` |

(Use `docker compose` instead of `docker-compose` if your Docker has the Compose plugin.) The optional wrapper adds flags on top:

| Command | Runs | Takes |
|---------|------|-------|
| `./scripts/run-tests.sh` | unit + API + integration (145 tests), with the coverage summary and HTML report | about 15 seconds |
| `./scripts/run-tests.sh --e2e` | only the end-to-end tests (15 tests) plus the sqlite3 database check; cleans up `./data-e2e` afterwards | about 1 minute |
| `./scripts/run-tests.sh --all` | default suite, then end-to-end plus the sqlite3 check | about 1.5 minutes |

The script exits non-zero if anything requested fails. Every run lists each test with its result and duration.

| Kind | Tests | What is real | What is faked |
|------|-------|--------------|---------------|
| Unit (`tests/.../Unit`) | 98 | the class under test | its collaborators, the clock |
| API (`tests/.../Api`) | 33 | routing, binding, validation, limiter, service, stats, JSON, status codes | repository (in-memory), clock |
| Integration (`tests/.../Integration`) | 14 | everything incl. Dapper and a real SQLite file, in-process | only the clock |
| End-to-end (`e2e/`) | 15 | the real Docker image, a real network, the mounted SQLite file, the production settings and the real clock | nothing |

* **Unit:** validator, rate limiter (fake clock, concurrency), the statistics cache (when the database is read, every strict rule, flush, failed flush, concurrent writers + flusher + reader), the stats repository and the flush service, the write gate, the ingest service (mocked repository, log levels, outcome recording inside the gate, invalidation after an ambiguous failure), repository and initializer (real temp SQLite file), connection factory, controller (mocked collaborators), `Reservation`, `ProblemResults` and `ApiBehaviorConfiguration`.
* **API:** every response of both endpoints over HTTP: 201/200 success variants, 400 (business rules, missing property, malformed JSON, wrong type, empty body), 415, 405, 404, 429 with `Retry-After`, 500, plus log-level assertions. Stats endpoint: duplicates and updates never increase `ingested`, the database is read once per supplier and then served from the cache, unknown suppliers are not cached, a graceful stop and the periodic flush save the counters.
* **Integration:** the full flow against a real SQLite file: create, duplicate, update, older timestamp (still updated) with database checks at each step, the real 100-request limit and window clearing (fake clock), parallel duplicates, per-supplier isolation, and data surviving a restart. Statistics over a real SQLite file: a graceful stop saves the counters and the next host reports them, the periodic flush writes while running, rows without saved counters still count as ingested, the cached count equals `SELECT COUNT(*)` after concurrent ingests and reads (the first read racing the first writes), and an insert that commits then fails does not leave a stale count.
* **End-to-end:** `docker-compose.e2e.yaml` builds the production image and starts it (own project name, own `./data-e2e` folder), then a test-runner container calls it over the compose network. Tests verify what was really persisted by reading the SQLite file (read-only) through the shared volume. The throttling test really sends 100 + 1 requests and waits for the true 60-second window to clear (hence the one minute). Each test uses a fresh supplier id so tests never share rate-limit windows. Two E2E tests cover the statistics: duplicates are not ingested, and the real container's periodic flush writes the counters into the mounted `SupplierStats` table. The E2E project is deliberately **not** in `Mize.sln`, which is how "with or without E2E" works. The test host console logs are silenced in API/integration tests; logs are captured in memory and asserted on instead.
* **Independent database check (`sqlite3-verify`):** after the E2E tests, a second container with the stand-alone `sqlite3` program (image `keinos/sqlite3`, pinned to 3.54.1; sqlite.org publishes no Docker image, so this is a widely used community one) opens the file the API wrote, read-only. It checks `PRAGMA integrity_check`, that rows exist, that the primary key is `(SupplierId, ReservationId)`, that no row has a negative price or `checkOut <= checkIn`, and that every timestamp is stored as UTC. After the statistics change it also checks that the `SupplierStats` table exists and that every supplier's saved `created` counter equals its reservation rows, which proves the container's graceful-shutdown flush ran (compose stops the API with SIGTERM when the test runner exits). This proves the file is a valid, portable SQLite database for a different SQLite build than the one bundled in .NET. It is a separate step because `docker-compose up --abort-on-container-exit` stops all containers the moment the test runner exits; `run-tests.sh --e2e` chains the two. It was also checked against a deliberately bad database to confirm it fails.
* **What E2E does not cover:** restarting the container (the test runner cannot control Docker). "Data survives a restart" is covered by integration tests (host stopped and started on the same file) and was also verified by hand with a real `docker stop`.

### Code coverage

Coverage is **always on**: `tests/Mize.Tests.csproj` sets `RunSettingsFilePath` to `tests/coverage.runsettings`, which enables the **Coverlet** collector (`coverlet.collector`, the .NET counterpart of JaCoCo). So every `dotnet test` of that project, with or without Docker and without any extra flag, writes a Cobertura XML file (to `tests/TestResults/`, git-ignored). It costs about half a second per run; pass `-p:RunSettingsFilePath=` to run once without it. It covers the unit, API and integration tests (the E2E project is separate and never included) and measures only the `Mize.Api` assembly.

The compose `tests` service goes one step further: after the tests it runs **ReportGenerator** (a .NET local tool pinned in `.config/dotnet-tools.json`, restored with `dotnet tool restore`) to print a text summary and write an HTML report to `coverage/report/index.html` (git-ignored). No minimum threshold is enforced. Current result: 98.5% line, 91.4% branch, 100% method coverage. The few uncovered lines are the failure-cleanup branch of `SqliteConnectionFactory` and an unreachable `default` branch in the controller. Coverage shows which lines ran, not whether the assertions are good.

The `tests` service sets `VSTEST_DISABLE_ARTIFACTS_DIRECTORY_VERIFICATION=1` because VSTest's temp-folder permission self-check fails inside the container (it only guards a temp directory).

### Manual testing scripts

`./scripts/run-api.sh` starts the API like production and `./scripts/ingest.sh` is the mini simulator (see Quick start). They are conveniences only: the compose files and `curl` do the same.

## Known limitations

* The rate-limit window is in memory by decision (see [Statistics](#statistics-sql-is-the-source-of-truth-memory-is-a-cache)): a restart starts a clean window and several instances would each have their own. The write gate and the statistics cache are per process, too: one instance must be the only writer of the database.
* After a hard crash the request counters can miss the last flush interval (5 seconds by default); `ingested` stays exact because it is read from the rows.
* Rate-limit entries and statistics-cache entries per supplier are kept for the process lifetime (no eviction), and a supplier id has no length limit. See [Known issue: unbounded memory per supplier id](#known-issue-unbounded-memory-per-supplier-id) for the explanation, the measurements and the fix options.
* No authentication: `supplierId` comes from the request body and is trusted.

### Known issue: unbounded memory per supplier id

**Status: open, documented for future reference. Nothing has been changed in the code for it.**

#### What it is

Two in-memory dictionaries are keyed by `supplierId`, and no code ever removes an entry (a search of `src/Mize.Api` finds no `TryRemove`,
`Remove` or `Clear`):

```
 SlidingWindowRateLimiter._windows   supplierId -> a queue of up to 100 timestamps   (created on the supplier's first request)
 SupplierStatsCache._entries         supplierId -> row count + counters              (created on its first recorded outcome)
```

Both live as long as the process does. A supplier whose 60-second window has long passed and whose queue is empty still keeps its entries.

#### Why it matters

* The `supplierId` comes from the request body and validation only checks that it is non-empty: **there is no maximum length**.
* The throttle does not protect against it. The limit is per supplier, so a caller that sends every request with a new supplier id gets a
  fresh quota of 100 each time, and every new id allocates new entries. The limiter amplifies the problem instead of preventing it.
* The service has no authentication, so anyone who can reach it can rotate ids.

#### Measured (real container, `mize-api` image)

| Test | Result |
|------|--------|
| 200 suppliers (warm baseline) | 42.7 MiB |
| after 20,000 more distinct short ids | 84.5 MiB (this batch also contained warm-up and not yet collected garbage, so it overstates the retained state) |
| after another 20,000 | 95.6 MiB, i.e. **+11 MiB, about 0.57 KB retained per supplier** |
| 300 requests, each with a new **100,000-character** supplier id | 95.6 -> 212.2 MiB, about **390 KB of growth per request** |

* A supplier with a short id costs roughly half a kilobyte, so a million suppliers would be around 0.6 GB (an extrapolation, not measured).
* A long id is the real hazard. 100,000 characters are about 200 KB as a .NET string (two bytes per character), kept as a dictionary key for the
  life of the process; the measured growth was higher because the large request buffers are large-object garbage that is collected late. The default
  request body limit is about 30 MB, so one 1 MB id would retain about 2 MB and a thousand of them about 2 GB. All 300 long-id requests were accepted
  with HTTP 201, and the same data is also stored in SQLite, so the disk grows as well.

#### What does not grow

* The timestamp queue per supplier is capped at the limit (100 entries) and the counters have a fixed size.
* A `GET` for an unknown supplier creates no cache entry (strict rule 5 of the statistics cache, covered by tests).
* Invalid requests are rejected before the limiter, so they allocate nothing.

#### Who can trigger it, and the consequence

Honest use is safe: a few dozen or hundred real suppliers cost nothing. A hostile or buggy client can exhaust memory. The likely outcome is an
out-of-memory kill followed by a restart (`restart: unless-stopped`), which clears every rate-limit window and loses up to one flush interval
(5 seconds by default) of request counters.

#### Fix options, cheapest first

| # | Option | What it does | Cost and trade-off |
|---|--------|--------------|--------------------|
| 1 | Maximum id length (for example 128 characters) | Rejects absurd ids with HTTP 400 | A few lines in the validator plus tests; removes the worst amplification (the 200 KB keys) |
| 2 | Sweep the limiter | A periodic task removes windows whose queue is empty; an empty window behaves exactly like no entry | Small; bounds limiter memory to recently active suppliers |
| 3 | Evict idle statistics-cache entries | Drop entries that are idle and fully flushed; SQL holds the truth, so the next read reloads them | Medium; must respect the strict cache rules (write gate, flush lock, entry lock), so it needs care and tests |
| 4 | Normalize ids (trim, case-insensitive) | Stops `acme`, `ACME` and `acme ` from being three suppliers | Changes how identity works, but it also closes the throttle bypass that comes from the same root cause |
| 5 | A registry of known suppliers | Unknown ids are rejected, so memory is bounded by the real number of suppliers | The real-world answer, but a product decision the specification does not define; it goes together with authentication |
| 6 | A container memory limit and an alert | Operational safety net | Does not fix the cause |

Recommended order when this is picked up: options 1 and 2 first (small and safe), option 3 if completeness matters, and option 4 together with the
throttle-bypass work. The specification states that production-level hardening is not expected, so this is a conscious, documented compromise for
the scoped exercise.

## Optional future work

* **Swagger / OpenAPI UI** (skipped for now). Add `Swashbuckle.AspNetCore` (pinned version), then in `Program.cs`:
  `builder.Services.AddEndpointsApiExplorer(); builder.Services.AddSwaggerGen();` and, inside an
  `if (app.Environment.IsDevelopment())` block, `app.UseSwagger(); app.UseSwaggerUI();`. Enabling `GenerateDocumentationFile`
  in the `.csproj` and passing the XML file to `AddSwaggerGen` would surface the existing `///` comments in the UI.
* Persist rate-limit state and stats (SQLite or Redis) to survive restarts and support multiple instances.
* Swap the logging provider (for example Serilog) — see [Logging](#logging).
