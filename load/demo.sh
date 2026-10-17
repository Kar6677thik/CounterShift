#!/usr/bin/env bash
# A two-minute tour of a live storage migration, the one recorded in docs/images/demo.svg.
#
#   docker compose -f deploy/compose/docker-compose.yml up -d --build      (or with the GHCR override)
#   load/demo.sh
#
# It edits deploy/compose/mode/storage-mode.json (restored at the end) and stops and starts the Compose
# Redis (step 5), so run it against a local stack only. Needs bash, curl and Python 3.
set -uo pipefail

cd "$(dirname "$0")/.."
export COMPOSE_FILE=deploy/compose/docker-compose.yml   # so "docker compose ..." finds the stack
export PYTHONIOENCODING=utf-8                           # the mode summary contains "→"
api=localhost:8080
mode_file=deploy/compose/mode/storage-mode.json
card="c_$RANDOM"   # a fresh card per run

# The first Python that actually runs (on Windows, "python3" can be a Microsoft Store placeholder).
for PY in python3 python; do "$PY" -c 'print()' >/dev/null 2>&1 && break; done
fmt() { "$PY" load/demo_format.py "$1"; }

say()  { printf '\n\033[1;36m# %s\033[0m\n' "$*"; sleep 1; }
show() { printf '\033[1;32m$\033[0m %s\n' "$1"; sleep 0.4; eval "$1"; }

# ── Thin wrappers around the HTTP API, so the commands on screen stay readable ─────────────────────
# events N CARD: POST /v1/events N times (payment.failed); prints the status codes.
events() {
  for _ in $(seq "$1"); do
    curl -s -o /dev/null -w '%{http_code}\n' -X POST $api/v1/events -H 'Content-Type: application/json' -d "{
      \"eventId\":\"$("$PY" -c 'import uuid;print(uuid.uuid4())')\",
      \"type\":\"payment.failed\",\"occurredAt\":\"$(date -u +%Y-%m-%dT%H:%M:%SZ)\",
      \"entities\":{\"cardId\":\"$2\",\"userId\":\"u_$2\"}}"
  done | sort | uniq -c | awk '{printf "  %s x HTTP %s\n", $1, $2}'
}
# decide CARD: POST /v1/decisions for a payment; prints the outcome and the card rule's arithmetic.
decide() {
  curl -s -w '\n%{http_code} %{time_total}' -X POST $api/v1/decisions -H 'Content-Type: application/json' \
    -d "{\"action\":\"payment\",\"entities\":{\"cardId\":\"$1\",\"userId\":\"u_$1\"}}" | fmt decision
}
# buckets CARD: GET /v1/counters: which pre-aggregated buckets answered "the last 10 minutes".
buckets() { curl -s "$api/v1/counters/payment_failed_by_card/$1?window=PT10M" | fmt buckets; }
# mode: GET /admin/storage-mode, one line.
mode() { curl -s $api/admin/storage-mode | fmt mode; }
ready() { curl -s -o /dev/null -w '  /readyz -> HTTP %{http_code}\n' $api/readyz; }
# set_mode "WRITE" "READ": edit the watched mode file, then wait for the API to apply the change (or refuse it).
set_mode() {
  local before; before=$(curl -s $api/admin/storage-mode | fmt version)
  "$PY" - "$mode_file" "$1" "$2" <<'PY'
import json, sys
path, write, read = sys.argv[1], sys.argv[2].split(), sys.argv[3].split()
w = {"Mode": write[0], "Primary": write[1], **({"Secondary": write[2]} if len(write) > 2 else {})}
r = {"Mode": read[0], "Primary": read[1], **({"Secondary": read[2], "Percent": float(read[3])} if len(read) > 2 else {})}
open(path, "w").write(json.dumps({"CounterShift": {"Storage": {"Write": w, "Read": r}}}, indent=2) + "\n")
PY
  for _ in $(seq 1 30); do
    sleep 0.5
    [ "$(curl -s $api/admin/storage-mode | fmt version)" != "$before" ] && { mode; return; }
  done
  printf '  \033[1;31mrefused\033[0m, still v%s: ' "$before"
  docker compose logs --since 30s api 2>/dev/null | grep -o "backend '[a-z]*' is read from but not written to[^;)]*" | tail -1
}

# ── Setup, not shown: Phase 0, Redis up, API warmed up ────────────────────────────────────────────
cp "$mode_file" /tmp/countershift-mode.bak
trap 'cp /tmp/countershift-mode.bak "$mode_file"; docker compose start redis >/dev/null 2>&1' EXIT
docker compose start redis >/dev/null 2>&1
"$PY" -c 'import json;open("'"$mode_file"'","w").write(json.dumps({"CounterShift":{"Storage":{"Write":{"Mode":"Single","Primary":"postgres"},"Read":{"Mode":"Single","Primary":"postgres"}}}},indent=2)+"
")'
docker compose restart api >/dev/null 2>&1   # fresh process: mode v1, parity counters at zero
for _ in $(seq 1 60); do [ "$(curl -s -o /dev/null -w '%{http_code}' $api/readyz)" = 200 ] && break; sleep 1; done
for _ in 1 2 3; do curl -s -o /dev/null $api/readyz; done

say "1. Counters live in PostgreSQL. Reading Redis before it has the data would silently allow fraud: refused"
show 'mode'
show 'set_mode "Single postgres" "Single redis"'

say "2. Phase 1: dual writes. Six failed payments on one card; the next payment is blocked"
show 'set_mode "Dual postgres redis" "Single postgres"'
show "events 6 $card"
show "decide $card"
show "buckets $card"

say "3. Phase 2: shadow-read Redis for 100% of counters and compare bucket by bucket"
show 'set_mode "Dual postgres redis" "Shadow postgres redis 100"'
show "decide $card; decide $card; sleep 1; mode"

say "4. Phase 3: Redis serves the reads. Same answer, from the new store"
show 'set_mode "Dual postgres redis" "Split postgres redis 100"'
show "decide $card"

say "5. Redis dies mid-migration: decisions fail closed, the instance reports not-ready"
show "docker compose stop redis 2>/dev/null"
show "decide $card"
show "ready"

say "6. Roll back with one config edit: reads return to PostgreSQL; writes keep flowing"
show 'set_mode "Dual postgres redis" "Single postgres"'
show "decide $card"
show "events 1 $card"
show "ready"

say "7. Redis comes back; nothing restarts"
show "docker compose start redis 2>/dev/null"
show "sleep 2; ready"
printf '\n'
