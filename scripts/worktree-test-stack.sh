#!/usr/bin/env bash
# Isolated per-worktree test stack on dynamic free ports (ASS-1715).
#
# Why this exists: a task run inside a git worktree needs its OWN backend (and
# optionally frontend) to run real integration / E2E tests, but the fixed dev
# (:5030/:4010) and stable (:5031/:4011) ports are already taken by the
# long-lived stacks. Two parallel worktree runs would also fight over fixed
# ports. This script brings up a fully isolated stack on DYNAMIC free ports,
# pointed at an isolated temp workspace so it can never become a second pickup
# driver on the shared workspace, hands the ports to tests via well-known env
# vars, and tears everything down cleanly.
#
# Commands:
#   up [--with-frontend] [--demo]   allocate free ports, boot backend (+frontend), write env file
#   down                   stop frontend + backend, remove isolated workspace + state
#   env                    print the stack env file (use: eval "$(./worktree-test-stack.sh env)")
#   status                 report health of the stack
#
# Env vars it EXPORTS into the stack env file (consumed by tests):
#   BACKEND_PORT    dynamic backend port
#   FRONTEND_PORT   dynamic frontend port (only with --with-frontend)
#   BACKEND_URL     http://127.0.0.1:$BACKEND_PORT
#   PW_BACKEND_URL  same as BACKEND_URL (frontend/e2e/helpers/api.ts precedence)
#   PW_BASE_URL     frontend URL for Playwright (frontend if served, else backend)
#   TaskRepository  isolated temp workspace the backend runs against
#
# Env vars it READS:
#   ATP_WORKTREE_TEST_ALLOW_LOCAL_CONFIG=1  proceed even if backend/appsettings.Local.json
#                                           exists (its WatchPaths could pull in shared projects)
#   WT_FRONTEND_BOOT_TIMEOUT  seconds to wait for ng serve (default 180)

set -u

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
STATE_DIR="${REPO_ROOT}/.worktree-test-stack"
ENV_FILE="${STATE_DIR}/stack.env"
WORKSPACE_LINK="${STATE_DIR}/workspace.path"
FE_PID_FILE="${STATE_DIR}/frontend.pid"
FE_LOG="${STATE_DIR}/frontend.log"
FIND_PORT="${SCRIPT_DIR}/find-free-port.mjs"
PROXY_CONFIG="proxy.dynamic.cjs"   # relative to frontend/

log() { echo "[worktree-test-stack] $*"; }
err() { echo "[worktree-test-stack] ERROR: $*" >&2; }

pid_alive() {
  local pid="$1"
  [[ -n "$pid" && "$pid" =~ ^[0-9]+$ ]] || return 1
  # `$!` is an MSYS process id under Git Bash, not the native Windows id
  # displayed by tasklist. `kill -0` understands that id on every supported
  # shell and therefore tracks the process we actually launched.
  kill -0 "$pid" 2>/dev/null
}

kill_tree() {
  local pid="$1"
  [[ -n "$pid" && "$pid" =~ ^[0-9]+$ ]] || return 0
  pkill -TERM -P "$pid" 2>/dev/null || true
  kill -TERM "$pid" 2>/dev/null || true
  sleep 0.3
  pkill -KILL -P "$pid" 2>/dev/null || true
  kill -KILL "$pid" 2>/dev/null || true
}

http_code() {
  curl -s -o /dev/null -w '%{http_code}' --max-time 2 "$1" 2>/dev/null || echo 000
}

# --- safety: refuse to boot against a shared workspace ----------------------
guard_local_config() {
  local local_cfg="${REPO_ROOT}/backend/appsettings.Local.json"
  if [[ -f "${local_cfg}" && "${ATP_WORKTREE_TEST_ALLOW_LOCAL_CONFIG:-}" != "1" ]]; then
    err "found ${local_cfg}."
    err "Its WatchPaths could make the isolated backend discover the SHARED workspace"
    err "and act as a second pickup driver. Remove it for this worktree, or set"
    err "ATP_WORKTREE_TEST_ALLOW_LOCAL_CONFIG=1 if you are certain its WatchPaths are empty/safe."
    exit 1
  fi
}

read_env_var() {
  # read_env_var KEY -> prints value from ENV_FILE (export KEY=VALUE lines)
  local key="$1"
  [[ -f "${ENV_FILE}" ]] || return 0
  sed -n "s/^export ${key}=//p" "${ENV_FILE}" | tail -n1
}

cmd_up() {
  local with_frontend=0
  local demo_mode=0
  for a in "$@"; do
    case "$a" in
      --with-frontend) with_frontend=1 ;;
      --demo) demo_mode=1 ;;
      *) err "unknown 'up' argument: $a"; exit 2 ;;
    esac
  done

  guard_local_config

  if [[ -f "${ENV_FILE}" ]]; then
    local existing_bp; existing_bp="$(read_env_var BACKEND_PORT)"
    if [[ -n "${existing_bp}" && "$(http_code "http://127.0.0.1:${existing_bp}/healthz")" == "200" ]]; then
      log "stack already up (backend :${existing_bp}). Run 'down' first to recycle."
      cmd_env
      return 0
    fi
    log "stale stack state found; cleaning up before bringing a fresh one up."
    cmd_down || true
  fi

  command -v node >/dev/null 2>&1 || { err "node not found on PATH"; exit 1; }
  mkdir -p "${STATE_DIR}"

  # Allocate distinct free ports in one shot.
  local want=1; [[ "${with_frontend}" -eq 1 ]] && want=2
  local ports; ports="$(node "${FIND_PORT}" --count "${want}")" || { err "port allocation failed"; exit 1; }
  local backend_port frontend_port
  backend_port="$(echo "${ports}" | awk '{print $1}')"
  frontend_port="$(echo "${ports}" | awk '{print $2}')"

  # Isolated workspace: empty by default, or populated exclusively with the
  # deterministic ADR-0056 demo projects for presentation capture.
  local workspace
  workspace="$(mktemp -d "${TMPDIR:-/tmp}/atp-worktree-test-XXXXXX")" || { err "mktemp failed"; exit 1; }
  echo "${workspace}" > "${WORKSPACE_LINK}"

  if [[ "${demo_mode}" -eq 1 ]]; then
    log "seeding ADR-0056 demo workspace"
    if ! node "${REPO_ROOT}/scripts/seed-demo-workspace.mjs" --root "${workspace}"; then
      err "demo workspace seed failed"
      cmd_down || true
      exit 1
    fi
  fi

  log "allocating: backend :${backend_port}$([[ ${with_frontend} -eq 1 ]] && echo ", frontend :${frontend_port}")"
  log "isolated workspace: ${workspace}"

  # Warm the build first. api.sh launches `dotnet run` then polls /healthz for
  # only 30s; a COLD compile blows past that window and api.sh would report a
  # false failure while the build keeps running orphaned. A prior `dotnet build`
  # makes the subsequent `dotnet run` an incremental no-op that binds in time.
  # The worktree's own bin is not shared, so no artifacts-path redirect needed.
  if command -v dotnet >/dev/null 2>&1; then
    log "pre-building backend (so dotnet run binds within api.sh's health window)..."
    if ! dotnet build "${REPO_ROOT}/backend/OrchestratorApi.csproj" -v quiet \
           > "${STATE_DIR}/backend-build.log" 2>&1; then
      err "backend build failed; see ${STATE_DIR}/backend-build.log"
      cmd_down || true
      exit 1
    fi
  else
    err "dotnet not found on PATH"; cmd_down || true; exit 1
  fi

  # --- boot backend via api.sh worktree mode (dynamic port, isolated repo) ---
  local demo_app="${workspace}/projects/demo-app"
  local demo_platform="${workspace}/projects/demo-platform"
  local backend_env=(
    "PORT=${backend_port}"
    "API_PORT_OVERRIDE=1"
    "ATP_WORKTREE_TEST_BACKEND=1"
    "TaskRepository=${workspace}"
    "Runner__Role=test-subject"
  )
  if [[ "${demo_mode}" -eq 1 ]]; then
    backend_env+=(
      "Security__Profile=public-demo-readonly"
      "WatchPaths__0__Name=Demo App"
      "WatchPaths__0__Path=${demo_app}"
      "WatchPaths__0__RootPath=${demo_app}"
      "WatchPaths__1__Name=Demo Platform"
      "WatchPaths__1__Path=${demo_platform}"
      "WatchPaths__1__RootPath=${demo_platform}"
    )
  fi
  if ! env "${backend_env[@]}" bash "${REPO_ROOT}/api.sh" start; then
    err "backend failed to start on :${backend_port}"
    cmd_down || true
    exit 1
  fi

  # --- write env file (backend known good now) ------------------------------
  local backend_url="http://127.0.0.1:${backend_port}"
  {
    echo "# Generated by scripts/worktree-test-stack.sh (ASS-1715). Do not edit."
    echo "export BACKEND_PORT=${backend_port}"
    echo "export BACKEND_URL=${backend_url}"
    echo "export PW_BACKEND_URL=${backend_url}"
    echo "export TaskRepository=${workspace}"
    if [[ "${demo_mode}" -eq 1 ]]; then
      echo "export Security__Profile=public-demo-readonly"
    fi
  } > "${ENV_FILE}"

  # --- optionally boot frontend (ng serve, dynamic proxy) -------------------
  if [[ "${with_frontend}" -eq 1 ]]; then
    if [[ ! -d "${REPO_ROOT}/frontend/node_modules" ]]; then
      err "frontend/node_modules missing - run 'bash scripts/npm-frontend.sh ci' first."
      cmd_down || true
      exit 1
    fi
    log "starting frontend dev server on :${frontend_port} (proxy -> ${backend_url})"
    : > "${FE_LOG}"
    (
      cd "${REPO_ROOT}/frontend" || exit 1
      BACKEND_PORT="${backend_port}" BACKEND_HOST="127.0.0.1" \
        nohup node ./node_modules/@angular/cli/bin/ng.js serve frontend \
          --port "${frontend_port}" \
          --host 127.0.0.1 \
          --proxy-config "${PROXY_CONFIG}" \
          > "${FE_LOG}" 2>&1 &
      echo $! > "${FE_PID_FILE}"
    )
    local fe_pid; fe_pid="$(tr -d ' \r\n' < "${FE_PID_FILE}" 2>/dev/null || true)"
    log "frontend PID ${fe_pid}; waiting for dev server..."
    local timeout="${WT_FRONTEND_BOOT_TIMEOUT:-180}"
    local waited=0 fe_url="http://127.0.0.1:${frontend_port}/"
    while (( waited < timeout )); do
      if [[ "$(http_code "${fe_url}")" =~ ^(200|30[0-9])$ ]]; then
        echo "export FRONTEND_PORT=${frontend_port}" >> "${ENV_FILE}"
        echo "export PW_BASE_URL=http://127.0.0.1:${frontend_port}" >> "${ENV_FILE}"
        log "frontend ready at ${fe_url}"
        break
      fi
      if ! pid_alive "${fe_pid}"; then
        err "frontend process exited early; see ${FE_LOG}"
        tail -n 80 "${FE_LOG}" >&2 || true
        cmd_down || true
        exit 1
      fi
      sleep 2; waited=$((waited + 2))
    done
    if (( waited >= timeout )); then
      err "frontend did not become ready within ${timeout}s; see ${FE_LOG}"
      tail -n 80 "${FE_LOG}" >&2 || true
      cmd_down || true
      exit 1
    fi
  else
    # No frontend: point Playwright's base URL at the backend so a spec that
    # only needs REST still has a usable PW_BASE_URL.
    echo "export PW_BASE_URL=${backend_url}" >> "${ENV_FILE}"
  fi

  log "stack up. Source the env into your test runner:"
  log "  eval \"\$(${SCRIPT_DIR}/worktree-test-stack.sh env)\""
  cmd_env
}

cmd_down() {
  local rc=0
  # frontend
  if [[ -f "${FE_PID_FILE}" ]]; then
    local fe_pid; fe_pid="$(tr -d ' \r\n' < "${FE_PID_FILE}" 2>/dev/null || true)"
    if [[ -n "${fe_pid}" ]]; then
      log "stopping frontend (PID ${fe_pid})"
      kill_tree "${fe_pid}"
    fi
    rm -f "${FE_PID_FILE}"
  fi
  # backend (delegate to api.sh stop with the recorded port)
  local bp; bp="$(read_env_var BACKEND_PORT)"
  if [[ -n "${bp}" ]]; then
    log "stopping backend (:${bp})"
    # API_PORT_OVERRIDE=1 so api.sh's port-pin guard accepts the dynamic port.
    PORT="${bp}" API_PORT_OVERRIDE=1 bash "${REPO_ROOT}/api.sh" stop || rc=1
  fi
  # isolated workspace
  if [[ -f "${WORKSPACE_LINK}" ]]; then
    local ws; ws="$(tr -d ' \r\n' < "${WORKSPACE_LINK}" 2>/dev/null || true)"
    if [[ -n "${ws}" && -d "${ws}" ]]; then
      case "${ws}" in
        *atp-worktree-test-*) log "removing isolated workspace ${ws}"; rm -rf "${ws}" ;;
        *) err "refusing to rm unexpected workspace path: ${ws}" ;;
      esac
    fi
    rm -f "${WORKSPACE_LINK}"
  fi
  # Remove all stack state wholesale (env, pids, logs, build log, workspace
  # pointer). STATE_DIR is the fixed ${REPO_ROOT}/.worktree-test-stack path, so
  # this is safe and leaves no residue behind.
  case "${STATE_DIR}" in
    */.worktree-test-stack) rm -rf "${STATE_DIR}" ;;
    *) err "unexpected state dir, not removing: ${STATE_DIR}" ;;
  esac
  log "stack down."
  return "${rc}"
}

cmd_env() {
  if [[ ! -f "${ENV_FILE}" ]]; then
    err "no stack env file; is the stack up? (${ENV_FILE})"
    exit 1
  fi
  cat "${ENV_FILE}"
}

cmd_status() {
  if [[ ! -f "${ENV_FILE}" ]]; then
    log "stack is down (no env file)."
    return 1
  fi
  local bp fp; bp="$(read_env_var BACKEND_PORT)"; fp="$(read_env_var FRONTEND_PORT)"
  local bc="n/a" fc="n/a"
  [[ -n "${bp}" ]] && bc="$(http_code "http://127.0.0.1:${bp}/healthz")"
  [[ -n "${fp}" ]] && fc="$(http_code "http://127.0.0.1:${fp}/")"
  log "backend :${bp:-?} -> healthz ${bc}; frontend :${fp:-none} -> ${fc}"
  [[ "${bc}" == "200" ]]
}

print_usage() {
  cat <<EOF

  worktree-test-stack.sh - isolated per-worktree test stack on dynamic ports

  Usage: ./scripts/worktree-test-stack.sh <command>

  Commands:
    up [--with-frontend] [--demo]
                           allocate free ports + boot backend (+ frontend);
                           --demo seeds and registers only ADR-0056 demo data
    down                   tear everything down, remove isolated workspace
    env                    print the stack env file (eval it in your runner)
    status                 report stack health

EOF
}

CMD="${1:-}"
shift || true
case "${CMD}" in
  up)     cmd_up "$@" ;;
  down)   cmd_down ;;
  env)    cmd_env ;;
  status) cmd_status ;;
  ""|-h|--help|help) print_usage; exit 0 ;;
  *) err "unknown command: '${CMD}'"; print_usage; exit 2 ;;
esac
