#!/usr/bin/env bash
# Dev backend lifecycle for Playwright-driven runs from stable.
#
# Why this exists: the dev checkout is the regression-test target. Its backend
# should be off by default and only come up when a Playwright spec running from
# stable needs to drive it. This script is the single, narrow path for that.
#
# Outside Playwright fixtures, prefer the parent devspace start-dev.sh /
# stop-dev.sh scripts when a human is debugging dev directly. This script
# deliberately handles only dev's backend on port 5030; it does not start the
# Angular dev server.
#
# Usage:
#   ./dev-lifecycle.sh start     # boot dev backend on :5030 (idempotent)
#   ./dev-lifecycle.sh stop      # shut dev backend down (idempotent)
#   ./dev-lifecycle.sh status    # exit 0 if healthy, 1 otherwise
#
# Env vars:
#   DEV_CHECKOUT   absolute path to the dev checkout (default: sibling folder
#                  agent-taskboard-dev next to the script's parent checkout).
#   DEV_PORT       backend port (default 5030).

set -u

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" && pwd)"
# scripts/supervisor/<this> -> repo root is two levels up.
THIS_CHECKOUT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
DEVSPACE_DIR="$(cd "${THIS_CHECKOUT}/.." && pwd)"

DEV_CHECKOUT="${DEV_CHECKOUT:-${DEVSPACE_DIR}/agent-taskboard-dev}"
DEV_PORT="${DEV_PORT:-5030}"
HEALTH_URL="http://127.0.0.1:${DEV_PORT}/healthz"

log() { echo "[dev-lifecycle] $*"; }

require_dev_checkout() {
  if [[ ! -d "${DEV_CHECKOUT}" ]]; then
    log "ERROR: dev checkout not found at ${DEV_CHECKOUT}"
    log "Set DEV_CHECKOUT to the dev checkout path."
    exit 2
  fi
  if [[ ! -x "${DEV_CHECKOUT}/api.sh" ]]; then
    log "ERROR: ${DEV_CHECKOUT}/api.sh missing or not executable"
    exit 2
  fi
}

is_healthy() {
  local code
  code="$(curl -s -o /dev/null -w '%{http_code}' --max-time 2 "${HEALTH_URL}" 2>/dev/null || echo 000)"
  [[ "${code}" == "200" ]]
}

cmd_status() {
  if is_healthy; then
    log "dev backend healthy at ${HEALTH_URL}"
    exit 0
  fi
  log "dev backend not healthy at ${HEALTH_URL}"
  exit 1
}

cmd_start() {
  require_dev_checkout
  if is_healthy; then
    log "dev backend already healthy at ${HEALTH_URL} (no-op)"
    exit 0
  fi
  log "starting dev backend in ${DEV_CHECKOUT} on :${DEV_PORT}"
  # ADR-0044: this script is the single legitimate path that brings dev's
  # backend up outside of an interactive operator session. Signal that to
  # start-dev.sh so its env-flag gate does not refuse the boot when this
  # script is in turn invoked from the Playwright dev-backend fixture.
  ( cd "${DEV_CHECKOUT}" && PORT="${DEV_PORT}" ATP_DEV_BACKEND_FROM_FIXTURE=1 ./api.sh start )
}

cmd_stop() {
  require_dev_checkout
  if ! is_healthy && ! [[ -f "${DEV_CHECKOUT}/.api.${DEV_PORT}.pid" ]]; then
    log "dev backend already stopped (no-op)"
    exit 0
  fi
  log "stopping dev backend in ${DEV_CHECKOUT}"
  ( cd "${DEV_CHECKOUT}" && PORT="${DEV_PORT}" ./api.sh stop ) || true
}

print_usage() {
  cat <<EOF

  dev-lifecycle.sh — start/stop/status the dev backend for Playwright runs

  Usage: ./dev-lifecycle.sh <start|stop|status>

  Env: DEV_CHECKOUT (default sibling agent-taskboard-dev),
       DEV_PORT (default 5030).

EOF
}

CMD="${1:-}"
case "${CMD}" in
  start)  cmd_start ;;
  stop)   cmd_stop ;;
  status) cmd_status ;;
  ""|-h|--help|help) print_usage; exit 0 ;;
  *) log "Unknown command: '${CMD}'"; print_usage; exit 2 ;;
esac
