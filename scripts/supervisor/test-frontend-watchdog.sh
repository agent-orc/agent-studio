#!/usr/bin/env bash
# Functional test for frontend-watchdog.sh (AGT-2829): proves the frontend
# comes back after being force-killed, and that repeated fast crashes make
# the supervisor give up with a clear, greppable message instead of looping
# forever. Skips with code 0 when python3 is not on PATH.

set -u

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
watchdog="${repo_root}/scripts/supervisor/frontend-watchdog.sh"

if ! command -v python3 >/dev/null 2>&1; then
  echo "test-frontend-watchdog: python3 not found; skipping." >&2
  exit 0
fi

test_root="$(mktemp -d)"
fail() {
  printf 'test-frontend-watchdog: FAIL: %s\n' "$*" >&2
  exit 1
}
cleanup() {
  FRONTEND_CHECKOUT="${test_root}/checkout" "${watchdog}" stop >/dev/null 2>&1 || true
  FRONTEND_CHECKOUT="${test_root}/crash-checkout" "${watchdog}" stop >/dev/null 2>&1 || true
  rm -rf "${test_root}"
}
trap cleanup EXIT

free_port() {
  python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1", 0)); print(s.getsockname()[1]); s.close()'
}

http_code() {
  curl -s -o /dev/null -w '%{http_code}' --max-time 2 "$1" 2>/dev/null || echo 000
}

wait_for() {
  local desc="$1" timeout="$2"; shift 2
  local waited=0
  while (( waited < timeout )); do
    if "$@"; then return 0; fi
    sleep 0.2
    waited=$(( waited + 1 ))
  done
  fail "timed out waiting for: ${desc}"
}

# --- fixture: a tiny HTTP server that answers 200 on every request ----------
cat > "${test_root}/fake-server.py" <<'EOF'
import http.server
import sys

port = int(sys.argv[1])

class Handler(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(200)
        self.end_headers()
        self.wfile.write(b"ok")

    def log_message(self, *args):
        pass

http.server.HTTPServer(("127.0.0.1", port), Handler).serve_forever()
EOF

# =============================================================================
# Scenario 1: kill the frontend, assert it comes back.
# =============================================================================
checkout="${test_root}/checkout"
mkdir -p "${checkout}/frontend"
port="$(free_port)"

env \
  FRONTEND_CHECKOUT="${checkout}" \
  FRONTEND_PORT="${port}" \
  FRONTEND_START_CMD="python3 ${test_root}/fake-server.py ${port}" \
  FRONTEND_MIN_UPTIME_SECONDS=3 \
  FRONTEND_BACKOFF_BASE_SECONDS=1 \
  FRONTEND_BACKOFF_MAX_SECONDS=2 \
  DETACH=1 \
  "${watchdog}" start >/dev/null

state_dir="${checkout}/.frontend-watchdog"
journal="${state_dir}/watchdog.log"
child_pid_file="${state_dir}/frontend.pid"

wait_for "frontend healthy after first start" 30 \
  bash -c "[ \"\$(curl -s -o /dev/null -w '%{http_code}' --max-time 2 http://127.0.0.1:${port}/ 2>/dev/null)\" = 200 ]"

FRONTEND_CHECKOUT="${checkout}" FRONTEND_PORT="${port}" "${watchdog}" status >/dev/null \
  || fail "status reported unhealthy right after start"

first_pid="$(cat "${child_pid_file}")"
[[ -n "${first_pid}" ]] || fail "no frontend pid recorded after start"

kill -KILL "${first_pid}" 2>/dev/null || fail "could not kill frontend pid ${first_pid}"

wait_for "restart logged after forced kill" 15 \
  grep -q "event=frontend_exited pid=${first_pid}" "${journal}"

wait_for "frontend healthy again after restart" 30 \
  bash -c "[ \"\$(curl -s -o /dev/null -w '%{http_code}' --max-time 2 http://127.0.0.1:${port}/ 2>/dev/null)\" = 200 ]"

second_pid="$(cat "${child_pid_file}" 2>/dev/null || true)"
[[ -n "${second_pid}" && "${second_pid}" != "${first_pid}" ]] \
  || fail "expected a new frontend pid after restart, got '${second_pid}' (first was ${first_pid})"

grep -q "event=frontend_started pid=${second_pid}" "${journal}" \
  || fail "restart start event missing from journal"

FRONTEND_CHECKOUT="${checkout}" FRONTEND_PORT="${port}" "${watchdog}" stop >/dev/null \
  || fail "stop command failed"

wait_for "supervisor pid file removed after stop" 10 \
  bash -c "[ ! -f '${state_dir}/supervisor.pid' ]"

if kill -0 "${second_pid}" 2>/dev/null; then
  fail "frontend process ${second_pid} still alive after stop"
fi

echo "test-frontend-watchdog: scenario 1 passed (kill -> restart -> stop)."

# =============================================================================
# Scenario 2: a frontend that crashes immediately every time makes the
# supervisor give up instead of restarting forever.
# =============================================================================
crash_checkout="${test_root}/crash-checkout"
mkdir -p "${crash_checkout}/frontend"

env \
  FRONTEND_CHECKOUT="${crash_checkout}" \
  FRONTEND_PORT="$(free_port)" \
  FRONTEND_START_CMD="false" \
  FRONTEND_MIN_UPTIME_SECONDS=5 \
  FRONTEND_MAX_FAST_CRASHES=3 \
  FRONTEND_BACKOFF_BASE_SECONDS=1 \
  FRONTEND_BACKOFF_MAX_SECONDS=1 \
  "${watchdog}" start >"${test_root}/crash-stdout.log" 2>&1
supervise_rc=$?

[[ "${supervise_rc}" -ne 0 ]] || fail "supervisor exited 0 after repeated fast crashes; expected non-zero"

crash_journal="${crash_checkout}/.frontend-watchdog/watchdog.log"
grep -q "event=supervisor_giving_up consecutive_fast_crashes=3" "${crash_journal}" \
  || fail "give-up event missing from journal after 3 fast crashes"
grep -q "ERROR: frontend crashed 3 times" "${test_root}/crash-stdout.log" \
  || fail "clear give-up message missing from stdout/stderr"

echo "test-frontend-watchdog: scenario 2 passed (repeated fast crashes -> give up with a clear message)."
