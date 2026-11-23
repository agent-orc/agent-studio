#!/usr/bin/env bash
# AGT-3988 Dossier capture. Run `npm --prefix frontend run e2e -- e2e/board/develop-state.spec.ts`
# once first so the prebuilt backend and frontend exist. Usage from a copy of this folder
# outside the repository: REPO_ROOT=<repo> CAPTURE_DIR=<out> bash run.sh
# The config import path must then point at $REPO_ROOT/frontend/playwright.config.
set -euo pipefail
: "${REPO_ROOT:?set REPO_ROOT}" "${CAPTURE_DIR:?set CAPTURE_DIR}"
FRONTEND_ROOT="$REPO_ROOT/frontend"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PORTS="$(node "${REPO_ROOT}/scripts/find-free-port.mjs" --count 2)"
read -r PW_E2E_BACKEND_PORT PW_E2E_FRONTEND_PORT <<< "${PORTS}"
PW_E2E_WORKSPACE="$(mktemp -d "${TMPDIR:-/tmp}/studio-e2e-XXXXXX")"
mkdir -p "${PW_E2E_WORKSPACE}/projects/agent-studio-worktree" "$CAPTURE_DIR"
PW_E2E_CHECKOUT="${PW_E2E_WORKSPACE}/checkout"
trap 'rm -rf "${PW_E2E_WORKSPACE}"' EXIT
export PW_E2E_BACKEND_PORT PW_E2E_FRONTEND_PORT PW_E2E_WORKSPACE PW_E2E_CHECKOUT CAPTURE_DIR FRONTEND_ROOT
export PW_E2E_PROFILE=standard PW_PREBUILT=1 CAPTURE_SPECS="$HERE"
export PW_BASE_URL="http://127.0.0.1:${PW_E2E_FRONTEND_PORT}" PW_BACKEND_URL="http://127.0.0.1:${PW_E2E_BACKEND_PORT}"
export DEV_PORT="${PW_E2E_BACKEND_PORT}" PW_E2E_TEST_START_MS="$(node -p 'Date.now()')"
cd "$HERE"
node "$FRONTEND_ROOT/node_modules/@playwright/test/cli.js" test --config "$HERE/playwright.config.ts"
