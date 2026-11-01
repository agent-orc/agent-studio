#!/usr/bin/env bash
# Pinned captures for the quality-studio-integration Dossier (AGT-3352).
# Requires a prebuilt frontend: npm --prefix frontend run build
set -euo pipefail
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
read -r DEV_PORT QSI_FRONTEND_PORT <<< "$(node "${repo_root}/scripts/find-free-port.mjs" --count 2)"
# api.sh pins 5030 by folder name; this capture uses an isolated dynamic port.
export DEV_PORT QSI_FRONTEND_PORT API_PORT_OVERRIDE=1
cd "${repo_root}/frontend"
node ./node_modules/@playwright/test/cli.js test \
  --config ../docs/quality-studio-integration/capture/playwright.config.mjs "$@"
