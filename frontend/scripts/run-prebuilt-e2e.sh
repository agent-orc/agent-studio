#!/usr/bin/env bash
# Build once per checkout, then launch a short-lived isolated Playwright stack.
set -euo pipefail

FRONTEND_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REPO_ROOT="$(cd "${FRONTEND_ROOT}/.." && pwd)"
PUBLISH_DIR="${REPO_ROOT}/backend/bin/e2e-publish"
CHECKOUT_SHA="$(git -C "${REPO_ROOT}" rev-parse HEAD)"

if [[ ! -f "${PUBLISH_DIR}/OrchestratorApi.dll" || "$(cat "${PUBLISH_DIR}/RELEASE-SHA" 2>/dev/null || true)" != "${CHECKOUT_SHA}" ]]; then
  dotnet publish "${REPO_ROOT}/backend/OrchestratorApi.csproj" --configuration Release --output "${PUBLISH_DIR}"
  printf '%s\n' "${CHECKOUT_SHA}" > "${PUBLISH_DIR}/RELEASE-SHA"
fi
if [[ ! -f "${FRONTEND_ROOT}/dist/frontend/browser/index.html" || "$(cat "${FRONTEND_ROOT}/dist/frontend/browser/.source-sha" 2>/dev/null || true)" != "${CHECKOUT_SHA}" ]]; then
  npm --prefix "${FRONTEND_ROOT}" run build
  printf '%s\n' "${CHECKOUT_SHA}" > "${FRONTEND_ROOT}/dist/frontend/browser/.source-sha"
fi
if [[ "$(git -C "${REPO_ROOT}" rev-parse HEAD)" != "${CHECKOUT_SHA}" ]]; then
  echo 'Checkout HEAD changed during prebuild. Rerun after the commit settles.' >&2
  exit 3
fi

PORTS="$(node "${REPO_ROOT}/scripts/find-free-port.mjs" --count 2)"
read -r PW_E2E_BACKEND_PORT PW_E2E_FRONTEND_PORT <<< "${PORTS}"
PW_E2E_WORKSPACE="$(mktemp -d "${TMPDIR:-/tmp}/studio-e2e-XXXXXX")"
mkdir -p "${PW_E2E_WORKSPACE}/projects/agent-studio"
cleanup() { rm -rf "${PW_E2E_WORKSPACE}"; }
trap cleanup EXIT
export PW_E2E_BACKEND_PORT PW_E2E_FRONTEND_PORT PW_E2E_WORKSPACE
export PW_BASE_URL="http://127.0.0.1:${PW_E2E_FRONTEND_PORT}"
export PW_BACKEND_URL="http://127.0.0.1:${PW_E2E_BACKEND_PORT}"
export DEV_PORT="${PW_E2E_BACKEND_PORT}"
export PW_PREBUILT=1
export PW_E2E_TEST_START_MS="$(node -p 'Date.now()')"

cd "${FRONTEND_ROOT}"
node ./node_modules/@playwright/test/cli.js test "$@"
