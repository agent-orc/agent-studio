#!/usr/bin/env sh
# Hardened resume helper for the stable / dev runner. Use after any
# operation that may have raced the backend lifecycle (an update-stable run,
# a manual `api.sh restart`, etc.) so the resume PUT cannot be silently lost.
#
# What this script does, in order:
#   1. Wait for the target backend's /healthz to return 200 (60 s ceiling
#      by default; backend may answer 503 for some seconds during startup
#      and we treat that as "still booting", not "give up").
#   2. Auto-register a `service` identity at /api/clients/register if the
#      caller did not pass an existing X-Client-Id. The store is idempotent
#      on displayName, so re-registering the same id returns the same id.
#   3. PUT /api/runner/<project>/mode with mode=auto-continuous, sending
#      X-Client-Id (mutations require it since the client-identity boundary
#      landed; without it the call returns 401 and the runner silently
#      stays paused — that is the regression this script exists to prevent).
#   4. GET /api/runner/status and verify the named project reports
#      Mode == auto-continuous. If not, retry the PUT with exponential
#      backoff up to ATP_RESUME_MAX_ATTEMPTS times.
#
# The script exits 0 on a verified resume. It exits non-zero when the
# resume could not be verified after all retries; the caller is expected
# to surface that as a high-severity advisory rather than continue.
#
# Usage:
#   resume-runner.sh <project>                     # read defaults from env
#   ATP_API=http://127.0.0.1:5031 \
#   ATP_CLIENT_ID=supervisor-session \
#     resume-runner.sh agent-taskboard
#
# Env vars:
#   ATP_API                 backend base URL (default: http://127.0.0.1:5031)
#   ATP_CLIENT_ID           explicit identity to send (default: auto-register
#                           a `service` identity called "stable-restart-watcher")
#   ATP_CLIENT_DISPLAY      displayName used during auto-registration
#                           (default: stable-restart-watcher)
#   ATP_RESUME_MAX_ATTEMPTS resume PUT + verify attempts (default: 5)
#   ATP_RESUME_BACKOFF      base seconds between retries; doubles each time
#                           (default: 2)
#   ATP_HEALTHZ_TIMEOUT     seconds to wait for /healthz=200 (default: 60)
#   AGENT_STUDIO_API_TOKEN  networked-profile operator token (skips local registration)
#
# Exit codes:
#   0  resume verified
#   2  fatal misconfiguration (missing project arg, missing curl)
#   3  /healthz never returned 200 within ATP_HEALTHZ_TIMEOUT
#   4  identity registration failed
#   5  resume could not be verified after ATP_RESUME_MAX_ATTEMPTS

set -eu

PROJECT="${1:-}"
if [ -z "$PROJECT" ]; then
  echo "[resume-runner] ERROR: usage: $0 <project>" >&2
  exit 2
fi
if ! command -v curl >/dev/null 2>&1; then
  echo "[resume-runner] ERROR: curl is required" >&2
  exit 2
fi

API="${ATP_API:-http://127.0.0.1:5031}"
CLIENT_ID="${ATP_CLIENT_ID:-}"
API_TOKEN="${AGENT_STUDIO_API_TOKEN:-}"
CLIENT_DISPLAY="${ATP_CLIENT_DISPLAY:-stable-restart-watcher}"
MAX_ATTEMPTS="${ATP_RESUME_MAX_ATTEMPTS:-5}"
BACKOFF="${ATP_RESUME_BACKOFF:-2}"
HEALTHZ_TIMEOUT="${ATP_HEALTHZ_TIMEOUT:-60}"

log() { printf '[resume-runner] %s\n' "$*" >&2; }

api_curl() {
  if [ -n "$API_TOKEN" ]; then curl -H "Authorization: Bearer $API_TOKEN" "$@"
  else curl "$@"; fi
}

# 1. Wait for healthz. Treat connect failures, 503, and other non-200 as
#    "still booting" until the timeout expires; only stop early on a clean
#    200 response.
wait_for_healthz() {
  start_epoch="$(date -u +%s)"
  while :; do
    code="$(curl -s -o /dev/null -w '%{http_code}' --max-time 3 "$API/healthz" || printf '000')"
    if [ "$code" = "200" ]; then
      log "healthz ok ($API/healthz)"
      return 0
    fi
    now_epoch="$(date -u +%s)"
    elapsed=$((now_epoch - start_epoch))
    if [ "$elapsed" -ge "$HEALTHZ_TIMEOUT" ]; then
      log "ERROR: /healthz never returned 200 within ${HEALTHZ_TIMEOUT}s (last code=$code)"
      return 1
    fi
    sleep 1
  done
}

# 2. Ensure we have a client id. Register a service identity if the caller
#    did not pass one. Re-registration with the same displayName is
#    idempotent in ClientIdentityStore so this is safe to call repeatedly.
ensure_client_id() {
  if [ -n "$API_TOKEN" ]; then CLIENT_ID="${CLIENT_ID:-local-default}"; return 0; fi
  if [ -n "$CLIENT_ID" ]; then
    return 0
  fi
  body="$(curl -s --max-time 5 \
    -H 'Content-Type: application/json' \
    -X POST "$API/api/clients/register" \
    -d "{\"displayName\":\"$CLIENT_DISPLAY\",\"kind\":\"service\"}" || printf '')"
  if [ -z "$body" ]; then
    log "ERROR: identity register returned empty response from $API/api/clients/register"
    return 1
  fi
  # Crude but jq-free: pull the first "id":"..." pair out of the response.
  CLIENT_ID="$(printf '%s' "$body" | sed -n 's/.*"id"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -n1)"
  if [ -z "$CLIENT_ID" ]; then
    log "ERROR: identity register response did not contain an id (body: $body)"
    return 1
  fi
  log "identity registered as '$CLIENT_ID' (displayName='$CLIENT_DISPLAY')"
}

# 3. Read the project's current mode out of /api/runner/status. Returns
#    the literal string ("auto-continuous", "paused", ...) or empty when
#    we cannot parse / reach the API.
read_project_mode() {
  body="$(api_curl -s --max-time 5 -H "X-Client-Id: $CLIENT_ID" "$API/api/runner/status" 2>/dev/null || printf '')"
  [ -n "$body" ] || { printf ''; return; }
  # Cut the JSON down to the fragment for the named project, then read the
  # first "mode":"..." that appears in it. Avoids pulling in jq for what is
  # supposed to be a tiny portable helper.
  printf '%s' "$body" \
    | tr ',' '\n' \
    | awk -v p="\"$PROJECT\"" '
        $0 ~ p { found=1 }
        found && /"mode"[[:space:]]*:[[:space:]]*"/ {
          match($0, /"mode"[[:space:]]*:[[:space:]]*"[^"]+"/);
          if (RSTART) {
            s = substr($0, RSTART, RLENGTH);
            sub(/.*"mode"[[:space:]]*:[[:space:]]*"/, "", s);
            sub(/".*/, "", s);
            print s;
            exit;
          }
        }
      '
}

# Percent-encode a string so it is safe to drop into a URL path segment.
# Pure POSIX-ish awk; we only depend on awk being available, which the
# rest of this script also assumes. Without this, project names with a
# space (e.g. "Agent Software Studio") produce a malformed URL and curl
# returns code 000 — the exact failure that motivated this hardening.
urlencode() {
  LC_ALL=C awk -v s="$1" '
    function ord(c,   n) {
      for (n = 0; n < 256; n++) if (sprintf("%c", n) == c) return n
      return 63   # "?", should never happen
    }
    BEGIN {
      out = ""
      for (i = 1; i <= length(s); i++) {
        c = substr(s, i, 1)
        if (c ~ /[A-Za-z0-9._~-]/) out = out c
        else                       out = out sprintf("%%%02X", ord(c))
      }
      print out
    }'
}

# 4. PUT the resume request and verify the mode flipped.
put_resume() {
  api_curl -s -o /dev/null -w '%{http_code}' --max-time 5 \
    -X PUT \
    -H 'Content-Type: application/json' \
    -H "X-Client-Id: $CLIENT_ID" \
    "$API/api/runner/$PROJECT_URL/mode" \
    -d '{"mode":"auto-continuous"}' || printf '000'
}

# --- main ---------------------------------------------------------------------

PROJECT_URL="$(urlencode "$PROJECT")"

log "starting; project=$PROJECT api=$API max=$MAX_ATTEMPTS"

if ! wait_for_healthz; then exit 3; fi
if ! ensure_client_id; then exit 4; fi

attempt=1
delay="$BACKOFF"
while :; do
  code="$(put_resume)"
  mode="$(read_project_mode)"
  if [ "$mode" = "auto-continuous" ]; then
    log "resume verified on attempt $attempt (PUT=$code, mode=$mode)"
    exit 0
  fi
  log "attempt $attempt/$MAX_ATTEMPTS: PUT=$code mode='$mode' (expected auto-continuous)"
  if [ "$attempt" -ge "$MAX_ATTEMPTS" ]; then
    log "ERROR: resume not verified after $attempt attempts (last mode='$mode'); project remains paused"
    exit 5
  fi
  sleep "$delay"
  attempt=$((attempt + 1))
  delay=$((delay * 2))
done
