#!/usr/bin/env bash
# Build once per checkout, then launch a short-lived isolated Playwright stack.
set -euo pipefail

FRONTEND_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REPO_ROOT="$(cd "${FRONTEND_ROOT}/.." && pwd)"
if [[ "${PW_E2E_SINGLE_RUN:-0}" != "1" && "$#" -eq 0 ]]; then
  result=0
  for profile in standard recovery demo; do
    PW_E2E_SINGLE_RUN=1 PW_E2E_PROFILE="${profile}" bash "$0" || result=1
  done
  exit "${result}"
fi
PW_E2E_PROFILE="${PW_E2E_PROFILE:-standard}"
for arg in "$@"; do
  case "${arg}" in
    *crash-recovery-prompt.spec.ts*) PW_E2E_PROFILE=recovery ;;
    *cockpit-integration-matrix.spec.ts*) PW_E2E_PROFILE=demo ;;
  esac
done
PUBLISH_DIR="${REPO_ROOT}/backend/bin/e2e-publish"
CHECKOUT_SHA="$(bash "${REPO_ROOT}/scripts/e2e-source-fingerprint.sh")"

if [[ ! -f "${PUBLISH_DIR}/OrchestratorApi.dll" || "$(cat "${PUBLISH_DIR}/E2E-FINGERPRINT" 2>/dev/null || true)" != "${CHECKOUT_SHA}" ]]; then
  dotnet publish "${REPO_ROOT}/backend/OrchestratorApi.csproj" --configuration Release --output "${PUBLISH_DIR}"
  git -C "${REPO_ROOT}" rev-parse HEAD > "${PUBLISH_DIR}/RELEASE-SHA"
  printf '%s\n' "${CHECKOUT_SHA}" > "${PUBLISH_DIR}/E2E-FINGERPRINT"
fi
if [[ ! -f "${FRONTEND_ROOT}/dist/frontend/browser/index.html" || "$(cat "${FRONTEND_ROOT}/dist/frontend/browser/.source-sha" 2>/dev/null || true)" != "${CHECKOUT_SHA}" ]]; then
  npm --prefix "${FRONTEND_ROOT}" run build
  printf '%s\n' "${CHECKOUT_SHA}" > "${FRONTEND_ROOT}/dist/frontend/browser/.source-sha"
fi
if [[ "$(bash "${REPO_ROOT}/scripts/e2e-source-fingerprint.sh")" != "${CHECKOUT_SHA}" ]]; then
  echo 'Checkout contents changed during prebuild. Rerun after the edit settles.' >&2
  exit 3
fi

PORTS="$(node "${REPO_ROOT}/scripts/find-free-port.mjs" --count 2)"
read -r PW_E2E_BACKEND_PORT PW_E2E_FRONTEND_PORT <<< "${PORTS}"
PW_E2E_WORKSPACE="$(mktemp -d "${TMPDIR:-/tmp}/studio-e2e-XXXXXX")"
mkdir -p "${PW_E2E_WORKSPACE}/projects/agent-studio-worktree"
PW_E2E_CHECKOUT="${PW_E2E_WORKSPACE}/checkout"
cleanup() { rm -rf "${PW_E2E_WORKSPACE}"; }
trap cleanup EXIT
export PW_E2E_BACKEND_PORT PW_E2E_FRONTEND_PORT PW_E2E_WORKSPACE PW_E2E_CHECKOUT PW_E2E_PROFILE
export PW_BASE_URL="http://127.0.0.1:${PW_E2E_FRONTEND_PORT}"
export PW_BACKEND_URL="http://127.0.0.1:${PW_E2E_BACKEND_PORT}"
export DEV_PORT="${PW_E2E_BACKEND_PORT}"
export PW_PREBUILT=1
export PW_E2E_TEST_START_MS="$(node -p 'Date.now()')"

cd "${FRONTEND_ROOT}"
node ./node_modules/@playwright/test/cli.js test "$@"
