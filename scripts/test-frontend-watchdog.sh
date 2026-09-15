#!/usr/bin/env bash
# Proof for scripts/frontend-watchdog.sh and the contract documented in
# docs/operations/setup/contributor-setup.md ("Reference: dev + stable
# side-by-side"): a killed frontend comes back, repeated fast crashes give up
# loudly instead of looping forever, and a start.sh-shaped wrapper wires the
# watchdog correctly under both DETACH=1 (background) and the foreground
# (interactive) start path, without touching backend start.
#
# This fails without scripts/frontend-watchdog.sh's supervision loop: a bare
# `ng serve` launched once by start.sh never comes back after a crash, which
# is the incident this script guards against.

set -Eeuo pipefail

script_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
watchdog="$script_dir/frontend-watchdog.sh"
test_root=$(mktemp -d 2>/dev/null || mktemp -d -t frontend-watchdog)

cleanup() {
  for pid_file in "$test_root"/*.pid "$test_root"/*/*.pid "$test_root"/*/*/*.pid; do
    [ -f "$pid_file" ] || continue
    pid=$(cat "$pid_file" 2>/dev/null) || continue
    [ -n "$pid" ] && kill -9 "$pid" 2>/dev/null || true
  done
  rm -rf -- "$test_root"
}
trap cleanup EXIT HUP INT TERM

find_free_port() {
  python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1", 0)); print(s.getsockname()[1]); s.close()'
}

port_up() {
  curl -sf --max-time 1 "http://127.0.0.1:$1/" >/dev/null 2>&1
}

wait_for() {
  # wait_for <max_attempts> <predicate...>
  attempts="$1"; shift
  i=0
  while [ "$i" -lt "$attempts" ]; do
    if "$@"; then return 0; fi
    sleep 0.2
    i=$((i + 1))
  done
  return 1
}

pid_gone() {
  ! kill -0 "$1" 2>/dev/null
}

# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------

fixtures=$test_root/fixtures
mkdir -p "$fixtures"

cat > "$fixtures/server.py" <<'EOF'
import http.server, socketserver, sys
port = int(sys.argv[1])
class Handler(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(200)
        self.end_headers()
        self.wfile.write(b"ok")
    def log_message(self, *a):
        pass
class Server(socketserver.TCPServer):
    allow_reuse_address = True
with Server(("127.0.0.1", port), Handler) as httpd:
    httpd.serve_forever()
EOF

cat > "$fixtures/crasher.sh" <<'EOF'
#!/usr/bin/env bash
printf 'simulated boot crash\n' >&2
exit 7
EOF
chmod +x "$fixtures/crasher.sh"

# ---------------------------------------------------------------------------
# Test A: a killed frontend comes back, and a clean stop actually stops it.
# ---------------------------------------------------------------------------

a_dir=$test_root/a
mkdir -p "$a_dir"
a_port=$(find_free_port)

"$watchdog" --cwd "$fixtures" --log-dir "$a_dir" \
  --restart-delay-seconds 1 --max-restart-delay-seconds 2 \
  --fast-crash-seconds 3 --max-fast-crashes 5 \
  -- python3 "$fixtures/server.py" "$a_port" \
  >"$a_dir/watchdog-stdout.log" 2>&1 &
a_watchdog_pid=$!
printf '%s\n' "$a_watchdog_pid" > "$a_dir/test-watchdog.pid"

wait_for 50 port_up "$a_port" || { echo "Test A: frontend never came up" >&2; exit 1; }

a_child_pid=$(cat "$a_dir/.frontend-watchdog.child.pid")
kill -9 "$a_child_pid"
wait_for 25 pid_gone "$a_child_pid" || { echo "Test A: killed child did not die" >&2; exit 1; }
# Prove the outage is real before asserting recovery (the 1s restart-delay
# floor makes this reliable): otherwise "it came back" could be trivially
# true if the port was never actually down.
if port_up "$a_port"; then
  echo "Test A: frontend still answering immediately after kill -9 (outage was not real)" >&2
  exit 1
fi

wait_for 50 port_up "$a_port" || { echo "Test A: frontend did not come back after kill -9" >&2; exit 1; }
grep -q 'event=child_exited pid='"$a_child_pid"' exit_code=' "$a_dir/frontend-watchdog.log" \
  || { echo "Test A: restart was not logged with an exit code" >&2; exit 1; }
grep -q 'event=child_started' "$a_dir/frontend-watchdog.log" \
  || { echo "Test A: restart was not logged with a new child" >&2; exit 1; }

kill -TERM "$a_watchdog_pid"
wait_for 25 pid_gone "$a_watchdog_pid" || { echo "Test A: watchdog ignored SIGTERM" >&2; exit 1; }
sleep 0.5
if port_up "$a_port"; then
  echo "Test A: frontend still answering after the watchdog was stopped" >&2
  exit 1
fi
[ -f "$a_dir/.frontend-watchdog.pid" ] && { echo "Test A: pid file survived a clean stop" >&2; exit 1; }

echo "Test A passed: kill -9 recovers, SIGTERM stops cleanly."

# ---------------------------------------------------------------------------
# Test B: repeated fast crashes back off, then give up loudly instead of
# spinning forever.
# ---------------------------------------------------------------------------

b_dir=$test_root/b
mkdir -p "$b_dir"

set +e
b_output=$("$watchdog" --cwd "$fixtures" --log-dir "$b_dir" \
  --restart-delay-seconds 1 --max-restart-delay-seconds 2 \
  --fast-crash-seconds 5 --max-fast-crashes 3 \
  -- "$fixtures/crasher.sh" 2>&1)
b_rc=$?
set -e

[ "$b_rc" -ne 0 ] || { echo "Test B: watchdog exited 0 after giving up" >&2; exit 1; }
printf '%s' "$b_output" | grep -q 'giving up after 3 consecutive fast crashes' \
  || { echo "Test B: give-up message missing or wrong count" >&2; exit 1; }
started_count=$(grep -c 'event=child_started' "$b_dir/frontend-watchdog.log")
[ "$started_count" -eq 3 ] || { echo "Test B: expected exactly 3 attempts before giving up, got $started_count" >&2; exit 1; }
grep -q 'event=child_exited .*exit_code=7' "$b_dir/frontend-watchdog.log" \
  || { echo "Test B: exit code 7 was not logged" >&2; exit 1; }
grep -q 'event=backoff_sleep seconds=1' "$b_dir/frontend-watchdog.log" \
  || { echo "Test B: initial backoff was not logged" >&2; exit 1; }
grep -q 'event=backoff_sleep seconds=2' "$b_dir/frontend-watchdog.log" \
  || { echo "Test B: backoff did not grow between attempts" >&2; exit 1; }
[ -f "$b_dir/.frontend-watchdog.pid" ] && { echo "Test B: pid file survived giving up" >&2; exit 1; }

echo "Test B passed: backoff grows, gives up after 3 fast crashes with a clear message."

# ---------------------------------------------------------------------------
# Test C / D: a start.sh-shaped wrapper, matching the documented contract
# (docs/operations/setup/contributor-setup.md), wires the watchdog under
# DETACH=1 (background) and the foreground (interactive) start path, and
# leaves the backend start (api.sh) untouched.
#
# start.sh / start-stable.sh live one level above both checkouts and are not
# versioned in this repository (see that doc); this fixture reproduces their
# documented shape rather than editing a real file that does not exist here.
# ---------------------------------------------------------------------------

c_devspace=$test_root/devspace
c_frontend_dir=$c_devspace/frontend
c_log_dir=$c_devspace/frontend-logs
mkdir -p "$c_frontend_dir" "$c_log_dir"
cp "$fixtures/server.py" "$c_frontend_dir/server.py"
c_port=$(find_free_port)
c_backend_marker=$c_devspace/backend-start-ran

cat > "$c_devspace/api.sh" <<'EOF'
#!/usr/bin/env sh
set -eu
[ "${1:-}" = start ] || exit 2
: > "$BACKEND_START_MARKER"
EOF
chmod +x "$c_devspace/api.sh"

cat > "$c_devspace/start.sh" <<EOF
#!/usr/bin/env sh
set -eu
"\$(dirname "\$0")/api.sh" start

watchdog_pid_file="$c_log_dir/.frontend-watchdog.pid"
run_watchdog() {
  # exec, not a plain call: in the foreground branch below this replaces
  # start.sh's own process with the watchdog, so a single stop signal sent
  # to the wrapper's pid reaches the watchdog directly instead of orphaning
  # it. In the backgrounded branch it collapses the forked subshell down to
  # just the watchdog for the same reason.
  exec "$watchdog" \\
    --cwd "$c_frontend_dir" --log-dir "$c_log_dir" \\
    --restart-delay-seconds 1 --max-restart-delay-seconds 2 \\
    --fast-crash-seconds 3 --max-fast-crashes 5 \\
    -- python3 "$c_frontend_dir/server.py" "$c_port"
}

if [ "\${DETACH:-0}" = 1 ]; then
  run_watchdog >/dev/null 2>&1 &
  disown 2>/dev/null || true
  i=0
  while [ ! -f "\$watchdog_pid_file" ] && [ "\$i" -lt 50 ]; do sleep 0.1; i=\$((i + 1)); done
else
  run_watchdog
fi
EOF
chmod +x "$c_devspace/start.sh"

# --- C: DETACH=1 backgrounds the watchdog; the wrapper returns quickly, the
# watchdog outlives it and keeps supervising. ---
BACKEND_START_MARKER="$c_backend_marker" DETACH=1 "$c_devspace/start.sh"

[ -f "$c_backend_marker" ] || { echo "Test C: backend start (api.sh) was not invoked" >&2; exit 1; }
wait_for 50 port_up "$c_port" || { echo "Test C: frontend never came up via start.sh DETACH=1" >&2; exit 1; }
[ -f "$c_log_dir/.frontend-watchdog.pid" ] || { echo "Test C: watchdog pid file missing under DETACH=1" >&2; exit 1; }

c_child_pid=$(cat "$c_log_dir/.frontend-watchdog.child.pid")
kill -9 "$c_child_pid"
wait_for 25 pid_gone "$c_child_pid" || { echo "Test C: killed frontend child did not die" >&2; exit 1; }
wait_for 50 port_up "$c_port" || { echo "Test C: frontend did not recover after start.sh DETACH=1" >&2; exit 1; }

# Per the documented contract, a stop targets the watchdog's own pid file,
# not a search for ng serve.
c_watchdog_pid=$(cat "$c_log_dir/.frontend-watchdog.pid")
kill -TERM "$c_watchdog_pid"
wait_for 25 pid_gone "$c_watchdog_pid" || { echo "Test C: watchdog ignored SIGTERM under DETACH=1" >&2; exit 1; }
if port_up "$c_port"; then
  echo "Test C: frontend still up after stopping the watchdog by its pid file" >&2
  exit 1
fi

echo "Test C passed: DETACH=1 backgrounds the watchdog; it recovers a killed frontend and stops via its pid file."

# --- D: without DETACH, start.sh runs the watchdog in the foreground, so an
# interactive stop (SIGINT/SIGTERM to the wrapper) stops the frontend too,
# leaving no orphan. ---
rm -f "$c_backend_marker"
d_port=$(find_free_port)
# The port is baked into start.sh by the heredoc above; regenerate the
# wrapper against a fresh port rather than mutating the first one in place.
cat > "$c_devspace/start.sh" <<EOF
#!/usr/bin/env sh
set -eu
"\$(dirname "\$0")/api.sh" start
exec "$watchdog" \\
  --cwd "$c_frontend_dir" --log-dir "$c_log_dir" \\
  --restart-delay-seconds 1 --max-restart-delay-seconds 2 \\
  --fast-crash-seconds 3 --max-fast-crashes 5 \\
  -- python3 "$c_frontend_dir/server.py" "$d_port"
EOF
chmod +x "$c_devspace/start.sh"
rm -f "$c_log_dir/.frontend-watchdog.pid" "$c_log_dir/.frontend-watchdog.child.pid" "$c_log_dir/frontend-watchdog.log"

BACKEND_START_MARKER="$c_backend_marker" "$c_devspace/start.sh" &
d_wrapper_pid=$!
printf '%s\n' "$d_wrapper_pid" > "$test_root/d-wrapper.pid"

wait_for 50 test -f "$c_backend_marker" || { echo "Test D: backend start (api.sh) was not invoked in the foreground path" >&2; exit 1; }
wait_for 50 port_up "$d_port" || { echo "Test D: frontend never came up via foreground start.sh" >&2; exit 1; }

kill -TERM "$d_wrapper_pid"
wait_for 25 pid_gone "$d_wrapper_pid" || { echo "Test D: foreground start.sh ignored SIGTERM" >&2; exit 1; }
if port_up "$d_port"; then
  echo "Test D: frontend orphaned after stopping the foreground start.sh wrapper" >&2
  exit 1
fi

echo "Test D passed: without DETACH, start.sh runs the watchdog in the foreground and a single stop signal tears down both."

printf '%s\n' 'frontend-watchdog tests passed'
