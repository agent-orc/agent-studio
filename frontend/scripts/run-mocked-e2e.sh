#!/usr/bin/env bash
# Run API-contract-mocked browser specs without starting a backend.
set -euo pipefail

FRONTEND_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REPO_ROOT="$(cd "${FRONTEND_ROOT}/.." && pwd)"
SHA="$(bash "${REPO_ROOT}/scripts/e2e-source-fingerprint.sh")"
if [[ "$(cat "${FRONTEND_ROOT}/dist/frontend/browser/.source-sha" 2>/dev/null || true)" != "${SHA}" ]]; then
  npm --prefix "${FRONTEND_ROOT}" run build
  printf '%s\n' "${SHA}" > "${FRONTEND_ROOT}/dist/frontend/browser/.source-sha"
fi
if [[ "$(bash "${REPO_ROOT}/scripts/e2e-source-fingerprint.sh")" != "${SHA}" ]]; then
  echo 'Checkout contents changed during the frontend prebuild. Rerun after the edit settles.' >&2
  exit 3
fi
PW_MOCKED_FRONTEND_PORT="$(node "${REPO_ROOT}/scripts/find-free-port.mjs")"
export PW_MOCKED_FRONTEND_PORT
export PW_BASE_URL="http://127.0.0.1:${PW_MOCKED_FRONTEND_PORT}"
export PW_PREBUILT=0
cd "${FRONTEND_ROOT}"
node ./node_modules/@playwright/test/cli.js test --config=playwright.mocked.config.ts "$@"
