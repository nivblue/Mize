#!/usr/bin/env bash
# Starts the API "like production" with the docker-compose.yaml of this repository, waits until it answers,
# and tells you what to do next. Everything runs in Docker: nothing has to be installed on your machine.
#
# Usage:
#   ./scripts/run-api.sh
#
# Stop it later with:  docker-compose down   (the database in ./data stays)

# Fail on errors, on unset variables, and on failures inside pipelines.
set -euo pipefail

# Always run from the repository root, no matter where the script is called from.
cd "$(dirname "$0")/.."

# Where the API listens (the port is published by docker-compose.yaml).
URL="http://localhost:8080"

# Prefer the Compose plugin ("docker compose"); fall back to the standalone "docker-compose" binary.
if docker compose version >/dev/null 2>&1; then
  COMPOSE=(docker compose)
elif command -v docker-compose >/dev/null 2>&1; then
  COMPOSE=(docker-compose)
else
  echo "Neither 'docker compose' nor 'docker-compose' was found. Please install Docker Compose." >&2
  exit 1
fi

echo "Starting the API with: ${COMPOSE[*]} up --build -d"
"${COMPOSE[@]}" up --build -d

# Wait (up to ~60 s) until the API answers, so the next command you type does not hit a half-started server.
echo -n "Waiting for the API"
for _ in $(seq 1 60); do
  if curl --silent --output /dev/null --fail "$URL/api/reservations/stats/readiness-probe"; then
    echo
    echo "API is up at $URL (database file: ./data/mize.db)"
    echo
    echo "Try it:      ./scripts/ingest.sh              (help for the mini simulator)"
    echo "Look inside: ${COMPOSE[*]} run --rm sqlite3   (sqlite3 shell, read-only)"
    echo "Logs:        ${COMPOSE[*]} logs -f api"
    echo "Stop:        ${COMPOSE[*]} down"
    exit 0
  fi
  echo -n "."
  sleep 1
done

echo
echo "The API did not answer within 60 seconds. Check the logs: ${COMPOSE[*]} logs api" >&2
exit 1
