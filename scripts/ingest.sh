#!/usr/bin/env bash
# Mini simulator for the reservation API: send reservations and read supplier statistics with curl.
# Start the API first with ./scripts/run-api.sh. Run without a command to see the help.

# Fail on unset variables and on failures inside pipelines. (-e is not set: curl failures are handled explicitly.)
set -uo pipefail

# Where the API listens (override with the BASE_URL environment variable).
BASE_URL="${BASE_URL:-http://localhost:8080}"

# Default values of the reservation; every one can be changed with a command-line option (see usage below).
SUPPLIER="acme"
RESERVATION_ID="res-1001"
ROOM="room-42"
CHECK_IN="2026-08-01T00:00:00Z"
CHECK_OUT="2026-08-03T00:00:00Z"
PRICE="450.00"
UPDATED_AT="2026-07-01T10:15:00Z"
COUNT=1

# Prints the help text.
usage() {
  cat <<HELP
Mini simulator for the reservation API  (API: $BASE_URL, change with BASE_URL=...)

Usage:
  ./scripts/ingest.sh reserve [options]     send a reservation (POST /api/reservations/ingest)
  ./scripts/ingest.sh stats [supplierId]    show ingested vs throttled counts (GET /api/reservations/stats/{supplierId})
  ./scripts/ingest.sh help                  show this help

Options of 'reserve' (defaults in brackets):
  --supplier ID       supplierId             [$SUPPLIER]
  --id ID             reservationId          [$RESERVATION_ID]
  --room ID           roomId                 [$ROOM]
  --check-in TIME     checkIn (UTC)          [$CHECK_IN]
  --check-out TIME    checkOut (UTC)         [$CHECK_OUT]
  --price NUMBER      price                  [$PRICE]
  --updated-at TIME   updatedAtUtc (UTC)     [$UPDATED_AT]
  --count N           send N requests; the id gets a -1, -2, ... suffix so each one is a new reservation,
                      and only a summary of the HTTP status codes is printed        [1]

Things to try (the API must be running):
  ./scripts/ingest.sh reserve                                  -> 201 created
  ./scripts/ingest.sh reserve                                  -> 200 unchanged (same details: nothing written)
  ./scripts/ingest.sh reserve --price 500 --updated-at 2026-07-02T09:00:00Z   -> 200 updated
  ./scripts/ingest.sh reserve --updated-at 2026-06-01T00:00:00Z               -> 200 updated (an older timestamp is still applied; the server logs a WARN)
  ./scripts/ingest.sh reserve --price -5                       -> 400 (validation error)
  ./scripts/ingest.sh reserve --supplier flood --count 105     -> 100 x 201, then 429 (rate limit: 100 per 60 s)
  ./scripts/ingest.sh stats flood                              -> ingested 100, throttled 5
HELP
}

# Builds the JSON request body for the reservation id given as $1, from the values above.
body_for() {
  cat <<JSON
{
  "supplierId": "$SUPPLIER",
  "reservationId": "$1",
  "roomId": "$ROOM",
  "checkIn": "$CHECK_IN",
  "checkOut": "$CHECK_OUT",
  "price": $PRICE,
  "updatedAtUtc": "$UPDATED_AT"
}
JSON
}

# Prints a hint when curl could not reach the API at all.
explain_curl_failure() {
  echo "Could not reach $BASE_URL. Is the API running? Start it with ./scripts/run-api.sh" >&2
}

# 'reserve' command: parses the options and sends one request (full response) or many (status summary).
cmd_reserve() {
  while [ $# -gt 0 ]; do
    case "$1" in
      --supplier)   SUPPLIER="${2:?--supplier needs a value}"; shift 2 ;;
      --id)         RESERVATION_ID="${2:?--id needs a value}"; shift 2 ;;
      --room)       ROOM="${2:?--room needs a value}"; shift 2 ;;
      --check-in)   CHECK_IN="${2:?--check-in needs a value}"; shift 2 ;;
      --check-out)  CHECK_OUT="${2:?--check-out needs a value}"; shift 2 ;;
      --price)      PRICE="${2:?--price needs a value}"; shift 2 ;;
      --updated-at) UPDATED_AT="${2:?--updated-at needs a value}"; shift 2 ;;
      --count)      COUNT="${2:?--count needs a value}"; shift 2 ;;
      *) echo "Unknown option for 'reserve': $1 (see: ./scripts/ingest.sh help)" >&2; exit 2 ;;
    esac
  done
  if ! [[ "$COUNT" =~ ^[1-9][0-9]*$ ]]; then
    echo "--count must be a positive whole number" >&2
    exit 2
  fi

  local url="$BASE_URL/api/reservations/ingest"

  if [ "$COUNT" = 1 ]; then
    echo "POST $url"
    body_for "$RESERVATION_ID"
    echo
    curl --silent --show-error --include --request POST "$url" \
      --header "Content-Type: application/json" --data "$(body_for "$RESERVATION_ID")" \
      || { explain_curl_failure; exit 1; }
    echo
    return
  fi

  echo "POST $url  x $COUNT  (supplier $SUPPLIER, ids $RESERVATION_ID-1 .. $RESERVATION_ID-$COUNT)"
  local codes="" code i
  for i in $(seq 1 "$COUNT"); do
    code=$(curl --silent --output /dev/null --write-out "%{http_code}" --request POST "$url" \
      --header "Content-Type: application/json" --data "$(body_for "$RESERVATION_ID-$i")") \
      || { explain_curl_failure; exit 1; }
    codes="$codes$code"$'\n'
  done
  echo "HTTP status summary:"
  printf '%s' "$codes" | sort | uniq -c | awk '{printf "  %s x %s\n", $2, $1}'
}

# 'stats' command: prints the counters of one supplier (default: the default supplier).
cmd_stats() {
  local supplier="${1:-$SUPPLIER}"
  local url="$BASE_URL/api/reservations/stats/$supplier"
  echo "GET $url"
  curl --silent --show-error "$url" || { explain_curl_failure; exit 1; }
  echo
}

# Dispatch on the first argument; without one, print the help.
case "${1:-}" in
  reserve)             shift; cmd_reserve "$@" ;;
  stats)               shift; cmd_stats "$@" ;;
  ""|help|-h|--help)   usage ;;
  *)                   echo "Unknown command: $1" >&2; echo >&2; usage >&2; exit 2 ;;
esac
