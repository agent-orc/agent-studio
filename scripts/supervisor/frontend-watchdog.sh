#!/usr/bin/env bash
# Frontend dev-server supervisor (AGT-2829).
#
# Why this exists: start-stable.sh / start-dev.sh spawn `ng serve` once,
# detached, and walk away. If that process dies - OOM, a crashed dev-server
# optimizer, an operator's stray taskkill - nothing brings it back, and the
# Stable seat answers 000 until a human notices and restarts it by hand. This
# script is the versioned, testable process outer wrapper scripts should
# delegate frontend startup to, the same relationship update-stable.sh has
# with start-stable.sh / stop-stable.sh: those wrappers live per machine, one
# level above the checkouts (see docs/operations/setup/contributor-setup.md),
# and delegate their real work to a script versioned in this repo.
#
# It deliberately launches `ng serve` directly (via node .../ng.js) instead of
# `npm start`: frontend/package.json's `prestart` runs the full lint suite,
# which would re-lint the whole frontend - and can hard-fail the boot on an
# unrelated lint violation - on every single restart.
#
# Usage:
#   ./frontend-watchdog.sh start     # supervise the frontend (idempotent)
#   ./frontend-watchdog.sh stop      # stop supervisor + frontend (idempotent)
#   ./frontend-watchdog.sh status    # exit 0 if healthy, 1 otherwise
#
# Env vars:
#   FRONTEND_CHECKOUT   checkout dir (default: this script's repo root)
#   FRONTEND_PORT       port to serve/health-check (default: 4011 for a
#                       *-stable checkout, 4010 otherwise - same convention
#                       as api.sh's backend DEFAULT_PORT)
#   FRONTEND_HOST       bind host (default: 127.0.0.1)
#   FRONTEND_START_CMD  full ng-serve command line override (default builds
#                       one from FRONTEND_CHECKOUT/FRONTEND_PORT/FRONTEND_HOST)
#   FRONTEND_MIN_UPTIME_SECONDS    below this, an exit counts as a "fast"
#                                  crash (default 10)
#   FRONTEND_MAX_FAST_CRASHES      consecutive fast crashes before giving up
#                                  (default 5)
#   FRONTEND_BACKOFF_BASE_SECONDS  first restart delay (default 2)
#   FRONTEND_BACKOFF_MAX_SECONDS   backoff ceiling (default 60)
#   DETACH=1                       start supervises in the background and
#                                  returns immediately (same convention as
#                                  update-stable.sh's start_script call)
#
# State (under FRONTEND_CHECKOUT/.frontend-watchdog/):
#   supervisor.pid   pid of the running supervise loop
#   frontend.pid     pid of the currently running frontend process
#   frontend.log     the frontend's own stdout/stderr
#   watchdog.log     one line per supervisor event (start, exit, restart,
#                    give-up), each with a UTC timestamp

set -u

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" && pwd)"
THIS_CHECKOUT="$(cd "${SCRIPT_DIR}/../.." && pwd)"

FRONTEND_CHECKOUT="${FRONTEND_CHECKOUT:-${THIS_CHECKOUT}}"
case "$(basename "${FRONTEND_CHECKOUT}")" in
  *-stable) DEFAULT_PORT=4011 ;;
  *)        DEFAULT_PORT=4010 ;;
esac
FRONTEND_PORT="${FRONTEND_PORT:-${DEFAULT_PORT}}"
FRONTEND_HOST="${FRONTEND_HOST:-127.0.0.1}"

STATE_DIR="${FRONTEND_CHECKOUT}/.frontend-watchdog"
PID_FILE="${STATE_DIR}/supervisor.pid"
CHILD_PID_FILE="${STATE_DIR}/frontend.pid"
LOG="${STATE_DIR}/frontend.log"
JOURNAL="${STATE_DIR}/watchdog.log"

MIN_UPTIME="${FRONTEND_MIN_UPTIME_SECONDS:-10}"
MAX_FAST_CRASHES="${FRONTEND_MAX_FAST_CRASHES:-5}"
BACKOFF_BASE="${FRONTEND_BACKOFF_BASE_SECONDS:-2}"
BACKOFF_MAX="${FRONTEND_BACKOFF_MAX_SECONDS:-60}"

DEFAULT_START_CMD="node '${FRONTEND_CHECKOUT}/frontend/node_modules/@angular/cli/bin/ng.js' serve frontend --host ${FRONTEND_HOST} --port ${FRONTEND_PORT} --proxy-config proxy.conf.json"
START_CMD="${FRONTEND_START_CMD:-${DEFAULT_START_CMD}}"

HEALTH_URL="http://${FRONTEND_HOST}:${FRONTEND_PORT}/"

log() { echo "[frontend-watchdog] $*"; }

iso_now() { date -u +%Y-%m-%dT%H:%M:%SZ; }

journal() { printf '%s [frontend-watchdog] %s\n' "$(iso_now)" "$*" >> "${JOURNAL}"; }

pid_alive() {
  local pid="$1"
  [[ -n "$pid" && "$pid" =~ ^[0-9]+$ ]] || return 1
  kill -0 "$pid" 2>/dev/null
}

# Best-effort: TERM the tree, then KILL anything still standing. `pkill` is
# absent on some Git Bash installs, so a missing binary must not abort the
# stop path (matches scripts/worktree-test-stack.sh's kill_tree).
kill_tree() {
  local pid="$1"
  [[ -n "$pid" && "$pid" =~ ^[0-9]+$ ]] || return 0
  pkill -TERM -P "$pid" 2>/dev/null || true
  kill -TERM "$pid" 2>/dev/null || true
  for _ in 1 2 3 4 5; do
    pid_alive "$pid" || return 0
    sleep 0.3
  done
  pkill -KILL -P "$pid" 2>/dev/null || true
  kill -KILL "$pid" 2>/dev/null || true
}

http_code() {
  curl -s -o /dev/null -w '%{http_code}' --max-time 2 "$1" 2>/dev/null || echo 000
}

require_frontend_dir() {
  if [[ ! -d "${FRONTEND_CHECKOUT}/frontend" ]]; then
    log "ERROR: ${FRONTEND_CHECKOUT}/frontend not found"
    exit 2
  fi
}

# Global, mutated by the trap handler so a caught INT/TERM can reach the
# currently running child instead of only flipping a flag `wait` never rechecks.
child_pid=""
stop_requested=0

handle_stop_signal() {
  stop_requested=1
  journal "event=stop_signal_received"
  [[ -n "${child_pid}" ]] && kill_tree "${child_pid}"
}

supervise() {
  require_frontend_dir
  mkdir -p "${STATE_DIR}"
  echo "$$" > "${PID_FILE}"
  trap 'rm -f "${PID_FILE}"' EXIT
  trap 'handle_stop_signal' INT TERM

  journal "event=supervisor_started port=${FRONTEND_PORT} host=${FRONTEND_HOST} min_uptime=${MIN_UPTIME}s max_fast_crashes=${MAX_FAST_CRASHES} backoff_base=${BACKOFF_BASE}s backoff_max=${BACKOFF_MAX}s"

  local fast_crashes=0
  local backoff="${BACKOFF_BASE}"

  while :; do
    local start_epoch; start_epoch=$(date +%s)
    ( cd "${FRONTEND_CHECKOUT}/frontend" && eval "exec ${START_CMD}" ) >> "${LOG}" 2>&1 &
    child_pid=$!
    echo "${child_pid}" > "${CHILD_PID_FILE}"
    journal "event=frontend_started pid=${child_pid} backoff_used=${backoff}s"

    wait "${child_pid}"
    local exit_code=$?
    local end_epoch; end_epoch=$(date +%s)
    local uptime=$(( end_epoch - start_epoch ))
    rm -f "${CHILD_PID_FILE}"

    if [[ "${stop_requested}" -eq 1 ]]; then
      journal "event=frontend_stopped_by_request pid=${child_pid} exit_code=${exit_code} uptime=${uptime}s"
      break
    fi

    local tail_lines; tail_lines=$(tail -n 20 "${LOG}" 2>/dev/null | tr '\n' '|')
    journal "event=frontend_exited pid=${child_pid} exit_code=${exit_code} uptime=${uptime}s log_tail=${tail_lines}"

    if (( uptime < MIN_UPTIME )); then
      fast_crashes=$((fast_crashes + 1))
      journal "event=fast_crash consecutive=${fast_crashes} threshold=${MAX_FAST_CRASHES}"
      if (( fast_crashes >= MAX_FAST_CRASHES )); then
        journal "event=supervisor_giving_up consecutive_fast_crashes=${fast_crashes}"
        log "ERROR: frontend crashed ${fast_crashes} times within ${MIN_UPTIME}s of starting each time; giving up. See ${LOG} and ${JOURNAL}."
        return 1
      fi
      backoff=$(( backoff * 2 ))
      (( backoff > BACKOFF_MAX )) && backoff=${BACKOFF_MAX}
    else
      fast_crashes=0
      backoff="${BACKOFF_BASE}"
    fi

    journal "event=restart_scheduled delay=${backoff}s"
    sleep "${backoff}" &
    local sleep_pid=$!
    wait "${sleep_pid}"
  done
  return 0
}

cmd_start() {
  require_frontend_dir
  mkdir -p "${STATE_DIR}"
  if [[ -f "${PID_FILE}" ]] && pid_alive "$(cat "${PID_FILE}" 2>/dev/null)"; then
    log "supervisor already running (pid $(cat "${PID_FILE}")); no-op"
    exit 0
  fi
  if [[ "${DETACH:-0}" == "1" ]]; then
    nohup bash "${SCRIPT_DIR}/frontend-watchdog.sh" __supervise__ >> "${JOURNAL}" 2>&1 &
    local detached_pid=$!
    for _ in $(seq 1 20); do
      [[ -f "${PID_FILE}" ]] && break
      sleep 0.1
    done
    log "started detached supervisor (pid ${detached_pid})"
    exit 0
  fi
  supervise
}

cmd_stop() {
  if [[ -f "${PID_FILE}" ]]; then
    local sup_pid; sup_pid="$(cat "${PID_FILE}" 2>/dev/null)"
    if pid_alive "${sup_pid}"; then
      log "stopping supervisor (pid ${sup_pid})"
      kill -TERM "${sup_pid}" 2>/dev/null || true
      for _ in $(seq 1 40); do
        pid_alive "${sup_pid}" || break
        sleep 0.2
      done
      pid_alive "${sup_pid}" && kill -KILL "${sup_pid}" 2>/dev/null || true
    fi
    rm -f "${PID_FILE}"
  else
    log "supervisor not running (no pid file)"
  fi
  if [[ -f "${CHILD_PID_FILE}" ]]; then
    kill_tree "$(cat "${CHILD_PID_FILE}" 2>/dev/null)"
    rm -f "${CHILD_PID_FILE}"
  fi
}

cmd_status() {
  local code; code="$(http_code "${HEALTH_URL}")"
  local sup_alive=0
  if [[ -f "${PID_FILE}" ]] && pid_alive "$(cat "${PID_FILE}" 2>/dev/null)"; then
    sup_alive=1
  fi
  log "supervisor pid-alive=${sup_alive}; frontend ${HEALTH_URL} -> ${code}"
  [[ "${sup_alive}" -eq 1 ]] && [[ "${code}" =~ ^(200|30[0-9])$ ]]
}

print_usage() {
  cat <<EOF

  frontend-watchdog.sh - supervise the frontend dev server with restart backoff

  Usage: ./frontend-watchdog.sh <start|stop|status>

  Env: FRONTEND_CHECKOUT (default: this repo), FRONTEND_PORT (default 4011 for
       a *-stable checkout, else 4010), FRONTEND_HOST (default 127.0.0.1),
       FRONTEND_START_CMD (override), FRONTEND_MIN_UPTIME_SECONDS (default 10),
       FRONTEND_MAX_FAST_CRASHES (default 5), FRONTEND_BACKOFF_BASE_SECONDS
       (default 2), FRONTEND_BACKOFF_MAX_SECONDS (default 60), DETACH=1.

EOF
}

CMD="${1:-}"
case "${CMD}" in
  start)         cmd_start ;;
  stop)          cmd_stop ;;
  status)        cmd_status ;;
  __supervise__) supervise ;;
  ""|-h|--help|help) print_usage; exit 0 ;;
  *) log "Unknown command: '${CMD}'"; print_usage; exit 2 ;;
esac
