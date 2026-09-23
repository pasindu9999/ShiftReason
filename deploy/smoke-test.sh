#!/usr/bin/env bash
# End-to-end check against a running ShiftReason: a local container or the live URL.
#
#   bash deploy/smoke-test.sh                          # http://localhost:8080
#   bash deploy/smoke-test.sh https://<app>.azurecontainerapps.io
#
# Unit tests cannot prove what this proves, because every one of these can pass
# on a developer machine and still fail in the image:
#
#   - the OR-Tools native library loads under the runtime image's glibc
#     (a solve actually runs, rather than the process dying on dlopen)
#   - the explanation path works: assumptions, single worker, conflict core
#   - SQLite can write under the non-root user (the run record is persisted)
#   - the built SPA, the demo recordings and the SignalR hub are all served
#
# Needs curl and jq, both preinstalled on GitHub's Ubuntu runners.

set -euo pipefail

BASE="${1:-http://localhost:8080}"
BASE="${BASE%/}"
# A scaled-to-zero Container App has to cold-start before it can answer.
READY_TIMEOUT="${READY_TIMEOUT:-180}"
SOLVE_TIMEOUT="${SOLVE_TIMEOUT:-180}"

step() { printf '\n==> %s\n' "$*"; }
fail() { printf 'FAIL: %s\n' "$*" >&2; exit 1; }

step "Waiting for $BASE/health (up to ${READY_TIMEOUT}s)"
deadline=$((SECONDS + READY_TIMEOUT))
until curl -fsS "$BASE/health" >/dev/null 2>&1; do
  (( SECONDS < deadline )) || fail "health endpoint never answered"
  sleep 3
done
echo "healthy after ${SECONDS}s"

step "SPA is served"
curl -fsS "$BASE/" | grep -q "<title>ShiftReason" || fail "index.html missing or not the ShiftReason build"
asset=$(curl -fsS "$BASE/" | grep -oE '/assets/[^"]+\.js' | head -1)
[[ -n "$asset" ]] || fail "no JS bundle referenced from index.html"
curl -fsS -o /dev/null "$BASE$asset" || fail "bundle $asset is not served"
echo "index.html and $asset"

step "Demo recordings are served"
count=$(curl -fsS "$BASE/demo/traces/index.json" | jq 'length')
(( count > 0 )) || fail "demo/traces/index.json is empty"
echo "$count recordings"

step "SignalR hub negotiates"
curl -fsS -X POST "$BASE/hubs/solve/negotiate?negotiateVersion=1" | jq -e '.connectionToken' >/dev/null \
  || fail "hub negotiate did not return a connection token"
echo "ok"

step "Presets"
curl -fsS "$BASE/api/presets" | jq -e 'map(.id) | index("night-crunch")' >/dev/null \
  || fail "night-crunch preset missing"
echo "ok"

step "Solve an impossible ward and wait for the explanation"
run_id=$(curl -fsS -X POST "$BASE/api/solve" \
  -H 'Content-Type: application/json' \
  -d '{"presetId":"night-crunch","seconds":10}' | jq -r '.runId')
[[ -n "$run_id" && "$run_id" != null ]] || fail "solve was not accepted"
echo "run $run_id"

deadline=$((SECONDS + SOLVE_TIMEOUT))
while :; do
  # 404 until the worker picks the job up; that is not a failure.
  run=$(curl -fsS "$BASE/api/runs/$run_id" 2>/dev/null || true)
  status=$(jq -r '.status // empty' <<<"$run" 2>/dev/null || true)
  explanation=$(jq -r '.explanationJson // empty' <<<"$run" 2>/dev/null || true)

  [[ "$status" == Failed ]] && fail "run failed: $(jq -r '.error' <<<"$run")"
  [[ "$status" == Infeasible && -n "$explanation" ]] && break
  (( SECONDS < deadline )) || fail "no explanation within ${SOLVE_TIMEOUT}s (last status: ${status:-none})"
  sleep 2
done

conflicts=$(jq '.conflictSet | length' <<<"$explanation")
timed_out=$(jq '.timedOut' <<<"$explanation")
[[ "$timed_out" == false ]] || fail "explanation timed out"
(( conflicts > 0 )) || fail "explanation has an empty conflict set"

echo "conflict set of $conflicts rule(s):"
jq -r '.conflictSet[].sentence | "  - " + .' <<<"$explanation"

step "PASS"
