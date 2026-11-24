#!/usr/bin/env bash
# AGT-4030 Dossier capture (docs/look-and-feel-overhaul).
# Prepare the prebuilt stack once from the repository root:
#   npm --prefix frontend run e2e -- e2e/settings/access-area.spec.ts
# Then: REPO_ROOT=<repo> CAPTURE_DIR=<out> bash docs/look-and-feel-overhaul/capture/run.sh
# The script copies this folder to a temporary directory outside the repository,
# links frontend/node_modules, writes a Playwright config that extends
# frontend/playwright.config.ts, and starts the isolated backend and frontend
# on free ports. Nothing is written into frontend/e2e.
set -euo pipefail
: "${REPO_ROOT:?set REPO_ROOT}" "${CAPTURE_DIR:?set CAPTURE_DIR}"
FRONTEND_ROOT="$REPO_ROOT/frontend"
SRC="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORK="$(mktemp -d "${TMPDIR:-/tmp}/lnf-capture-XXXXXX")"
PORTS="$(node "${REPO_ROOT}/scripts/find-free-port.mjs" --count 2)"
read -r PW_E2E_BACKEND_PORT PW_E2E_FRONTEND_PORT <<< "${PORTS}"
PW_E2E_WORKSPACE="$(mktemp -d "${TMPDIR:-/tmp}/studio-e2e-XXXXXX")"
mkdir -p "${PW_E2E_WORKSPACE}/projects/agent-studio-worktree" "$CAPTURE_DIR"
PW_E2E_CHECKOUT="${PW_E2E_WORKSPACE}/checkout"
trap 'rm -rf "${PW_E2E_WORKSPACE}" "${WORK}"' EXIT
cp "$SRC/look-and-feel-capture.spec.ts" "$WORK/"
ln -s "$FRONTEND_ROOT/node_modules" "$WORK/node_modules"
cat > "$WORK/playwright.config.ts" <<EOF
import base from '${FRONTEND_ROOT}/playwright.config';
const servers = Array.isArray(base.webServer) ? base.webServer : base.webServer ? [base.webServer] : [];
export default {
  ...base,
  testDir: '${WORK}',
  reporter: [['list']],
  retries: 0,
  globalSetup: '${FRONTEND_ROOT}/e2e/prebuilt-global-setup.ts',
  webServer: servers.map(server => ({ ...server, cwd: '${FRONTEND_ROOT}' })),
  projects: [{ name: 'chromium', use: { browserName: 'chromium' } }],
};
EOF
export PW_E2E_BACKEND_PORT PW_E2E_FRONTEND_PORT PW_E2E_WORKSPACE PW_E2E_CHECKOUT CAPTURE_DIR FRONTEND_ROOT
export PW_E2E_PROFILE=standard PW_PREBUILT=1
export PW_BASE_URL="http://127.0.0.1:${PW_E2E_FRONTEND_PORT}" PW_BACKEND_URL="http://127.0.0.1:${PW_E2E_BACKEND_PORT}"
export DEV_PORT="${PW_E2E_BACKEND_PORT}" PW_E2E_TEST_START_MS="$(node -p 'Date.now()')"
cd "$WORK"
node "$FRONTEND_ROOT/node_modules/@playwright/test/cli.js" test --config "$WORK/playwright.config.ts"
