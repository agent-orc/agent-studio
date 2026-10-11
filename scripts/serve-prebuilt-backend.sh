#!/usr/bin/env bash
# Playwright webServer entry point. The caller supplies an isolated workspace.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
: "${PW_E2E_BACKEND_PORT:?Set PW_E2E_BACKEND_PORT}"
: "${PW_E2E_WORKSPACE:?Set PW_E2E_WORKSPACE}"

export PORT="${PW_E2E_BACKEND_PORT}"
export API_PORT_OVERRIDE=1
export ATP_WORKTREE_TEST_BACKEND=1
export API_PREBUILT_DIR="${REPO_ROOT}/backend/bin/e2e-publish"
export API_REQUIRE_PREBUILT=1
export API_START_TIMEOUT_SECS="${API_START_TIMEOUT_SECS:-90}"
export TaskRepository="${PW_E2E_WORKSPACE}"
export Runner__Role=test-subject
export WatchPaths__0__Name='Playwright Workspace'
export WatchPaths__0__Path="${PW_E2E_WORKSPACE}/projects/agent-studio"
export WatchPaths__0__RootPath="${REPO_ROOT}"
export WatchPaths__0__RepositoryPath="${REPO_ROOT}"

stop() { "${REPO_ROOT}/api.sh" stop; }
trap stop EXIT
trap 'exit 0' TERM INT
"${REPO_ROOT}/api.sh" start
while curl --silent --fail --max-time 2 "http://127.0.0.1:${PORT}/healthz" >/dev/null; do
  sleep 2
done
exit 1
