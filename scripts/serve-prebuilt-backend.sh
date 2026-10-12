#!/usr/bin/env bash
# Playwright webServer entry point. The caller supplies an isolated workspace.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
: "${PW_E2E_BACKEND_PORT:?Set PW_E2E_BACKEND_PORT}"
: "${PW_E2E_WORKSPACE:?Set PW_E2E_WORKSPACE}"
: "${PW_E2E_CHECKOUT:?Set PW_E2E_CHECKOUT}"

if [[ -f "${REPO_ROOT}/backend/appsettings.Local.json" && "${ATP_WORKTREE_TEST_ALLOW_LOCAL_CONFIG:-}" != "1" ]]; then
  echo 'Refusing appsettings.Local.json: its WatchPaths could reach the shared workspace.' >&2
  exit 1
fi

backend_started=0
stop() {
  if (( backend_started )); then
    "${REPO_ROOT}/api.sh" stop || true
  fi
  git -C "${REPO_ROOT}" worktree remove --force "${PW_E2E_CHECKOUT}" || true
}
trap stop EXIT
trap 'exit 0' TERM INT
git -C "${REPO_ROOT}" worktree add --detach "${PW_E2E_CHECKOUT}" HEAD

export PORT="${PW_E2E_BACKEND_PORT}"
export API_PORT_OVERRIDE=1
export ATP_WORKTREE_TEST_BACKEND=1
export API_PREBUILT_DIR="${REPO_ROOT}/backend/bin/e2e-publish"
export API_REQUIRE_PREBUILT=1
export API_START_TIMEOUT_SECS="${API_START_TIMEOUT_SECS:-90}"
export TaskRepository="${PW_E2E_WORKSPACE}"
export Runner__Role=test-subject
export WatchPaths__0__Name='Agent Studio Worktree'
export WatchPaths__0__Path="${PW_E2E_WORKSPACE}/projects/agent-studio-worktree"
export WatchPaths__0__RootPath="${PW_E2E_CHECKOUT}"
export WatchPaths__0__RepositoryPath="${PW_E2E_CHECKOUT}"

case "${PW_E2E_PROFILE:-standard}" in
  standard) ;;
  recovery)
    recovery_repo="${PW_E2E_WORKSPACE}/recovery-repository"
    mkdir -p "${recovery_repo}"
    git -C "${recovery_repo}" init -q
    git -C "${recovery_repo}" config user.name 'Playwright Recovery'
    git -C "${recovery_repo}" config user.email 'playwright-recovery@example.invalid'
    printf 'before restart\n' > "${recovery_repo}/recovery-proof.txt"
    git -C "${recovery_repo}" add .
    git -C "${recovery_repo}" commit -qm 'test: recovery baseline'
    printf 'pending operator decision\n' > "${recovery_repo}/recovery-proof.txt"
    export WatchPaths__0__RootPath="${recovery_repo}"
    export WatchPaths__0__RepositoryPath="${recovery_repo}"
    ;;
  demo)
    node "${REPO_ROOT}/scripts/seed-demo-workspace.mjs" --root "${PW_E2E_WORKSPACE}"
    export WatchPaths__0__Name='Demo App'
    export WatchPaths__0__Path="${PW_E2E_WORKSPACE}/projects/demo-app"
    export WatchPaths__0__RootPath="${WatchPaths__0__Path}"
    export WatchPaths__0__RepositoryPath="${WatchPaths__0__Path}"
    export WatchPaths__1__Name='Demo Platform'
    export WatchPaths__1__Path="${PW_E2E_WORKSPACE}/projects/demo-platform"
    export WatchPaths__1__RootPath="${WatchPaths__1__Path}"
    export WatchPaths__1__RepositoryPath="${WatchPaths__1__Path}"
    export DeliveryChain__Guarded=false
    export ReviewDecisionOrchestrator__BootDelaySeconds=3600
    ;;
  *) echo "Unknown PW_E2E_PROFILE: ${PW_E2E_PROFILE}" >&2; exit 2 ;;
esac

"${REPO_ROOT}/api.sh" start
backend_started=1
failures=0
while true; do
  sleep 2
  if curl --silent --fail --max-time 2 "http://127.0.0.1:${PORT}/healthz" >/dev/null; then
    failures=0
    continue
  fi
  failures=$((failures + 1))
  if (( failures < 5 )); then
    continue
  fi
  backend_pid="$(cat "${REPO_ROOT}/.api.pid" 2>/dev/null || true)"
  if [[ "${backend_pid}" =~ ^[0-9]+$ ]] && kill -0 "${backend_pid}" 2>/dev/null; then
    failures=0
    continue
  fi
  echo 'Prebuilt backend stopped or failed five consecutive health probes.' >&2
  exit 1
done
