#!/usr/bin/env bash
# Sends one reservation to POST /api/reservations/ingest, then prints the supplier's stats.
#
# Usage:
#   ./scripts/ingest.sh                          # uses the local compose API (http://localhost:5080)
#   BASE_URL=http://localhost:8080 ./scripts/ingest.sh   # target the main compose API instead
#
# Edit the BODY below to try other cases (new reservation, changed price, older updatedAtUtc, invalid data...).
# Run it several times to see "created" -> "unchanged", and more than RateLimit:MaxRequests times to see HTTP 429.

# Where the API listens (override with the BASE_URL environment variable).
BASE_URL="${BASE_URL:-http://localhost:5080}"

# The request body. All timestamps must be UTC; checkOut must be after checkIn; price must be >= 0.
BODY='{
  "supplierId": "acme",
  "reservationId": "res-1001",
  "roomId": "room-42",
  "checkIn": "2026-08-01T00:00:00Z",
  "checkOut": "2026-08-03T00:00:00Z",
  "price": 450.00,
  "updatedAtUtc": "2026-07-01T10:15:00Z"
}'

# Supplier to report stats for (kept in sync with the body above).
SUPPLIER_ID="acme"

echo "POST $BASE_URL/api/reservations/ingest"
curl --silent --show-error --include \
  --request POST "$BASE_URL/api/reservations/ingest" \
  --header "Content-Type: application/json" \
  --data "$BODY"

echo
echo
echo "GET $BASE_URL/api/reservations/stats/$SUPPLIER_ID"
curl --silent --show-error "$BASE_URL/api/reservations/stats/$SUPPLIER_ID"
echo
