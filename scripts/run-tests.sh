#!/usr/bin/env bash
# Runs the test suites inside Docker (no local .NET installation needed).
#
# Usage:
#   ./scripts/run-tests.sh              unit + API + integration tests, with a coverage summary and HTML report   [default]
#   ./scripts/run-tests.sh --e2e        ONLY the end-to-end tests against the real Docker image (~1 min)
#   ./scripts/run-tests.sh --all        the default suite, then the end-to-end tests
#   ./scripts/run-tests.sh --help
#
# Exit code is 0 only when everything that was requested passed (so it can be used in CI).
# Coverage is collected on every default run (it is configured in the test project) and never includes the E2E tests:
# they run against a container, not in-process.

# Fail on unset variables and on failures inside pipelines. (-e is not set: failures are collected explicitly.)
set -uo pipefail

# Always run from the repository root, no matter where the script is called from.
cd "$(dirname "$0")/.."

# What to run (default: only the fast suite).
RUN_MAIN=1
RUN_E2E=0

# Parse the command-line flags.
for arg in "$@"; do
  case "$arg" in
    --e2e)      RUN_MAIN=0; RUN_E2E=1 ;;
    --all)      RUN_MAIN=1; RUN_E2E=1 ;;
    -h|--help)  sed -n '2,12p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *)          echo "Unknown option: $arg (try --help)" >&2; exit 2 ;;
  esac
done

# Prefer the Compose plugin ("docker compose"); fall back to the standalone "docker-compose" binary.
if docker compose version >/dev/null 2>&1; then
  COMPOSE=(docker compose)
elif command -v docker-compose >/dev/null 2>&1; then
  COMPOSE=(docker-compose)
else
  echo "Neither 'docker compose' nor 'docker-compose' was found. Please install Docker Compose." >&2
  exit 1
fi

echo "Using: ${COMPOSE[*]}"

# Collected exit status: stays 0 unless a requested suite fails.
STATUS=0

# Unit + API + integration tests; coverage is collected and reported as part of this run.
run_main_suite() {
  echo "==> Unit + API + integration tests (with coverage)"
  "${COMPOSE[@]}" -f docker-compose.tests.yaml run --rm tests
}

# End-to-end tests: start the API from the real Dockerfile, run the tests against it, then check the resulting
# database file with the stand-alone sqlite3 container, and always clean up.
run_e2e_suite() {
  local e2e=("${COMPOSE[@]}" -f docker-compose.e2e.yaml)
  echo "==> End-to-end tests against the real Docker image (takes about a minute: the real rate-limit window is awaited)"
  rm -rf data-e2e
  "${e2e[@]}" up --build --abort-on-container-exit --exit-code-from e2e-tests --attach e2e-tests --no-log-prefix
  local rc=$?
  if [ "$rc" = 0 ]; then
    echo "==> Independent check of the database file with the stand-alone sqlite3 program"
    "${e2e[@]}" --profile verify run --rm sqlite3-verify
    rc=$?
  fi
  "${e2e[@]}" down --volumes --remove-orphans >/dev/null 2>&1
  rm -rf data-e2e
  return $rc
}

if [ "$RUN_MAIN" = 1 ]; then
  run_main_suite || STATUS=$?
fi

if [ "$RUN_E2E" = 1 ]; then
  run_e2e_suite || STATUS=$?
fi

if [ "$STATUS" = 0 ]; then
  echo "All requested test suites passed."
else
  echo "Some test suites FAILED (exit code $STATUS)." >&2
fi
exit "$STATUS"
