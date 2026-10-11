#!/usr/bin/env bash
# Run API-contract-mocked browser specs without starting a backend.
set -euo pipefail

FRONTEND_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REPO_ROOT="$(cd "${FRONTEND_ROOT}/.." && pwd)"
SHA="$(git -C "${REPO_ROOT}" rev-parse HEAD)"
if [[ "$(cat "${FRONTEND_ROOT}/dist/frontend/browser/.source-sha" 2>/dev/null || true)" != "${SHA}" ]]; then
  echo 'Prebuilt frontend is missing or stale. Run npm --prefix frontend run e2e once to prepare it.' >&2
  exit 2
fi
PW_MOCKED_FRONTEND_PORT="$(node "${REPO_ROOT}/scripts/find-free-port.mjs")"
export PW_MOCKED_FRONTEND_PORT
export PW_BASE_URL="http://127.0.0.1:${PW_MOCKED_FRONTEND_PORT}"
export PW_PREBUILT=0
cd "${FRONTEND_ROOT}"
node ./node_modules/@playwright/test/cli.js test --config=playwright.mocked.config.ts "$@"
