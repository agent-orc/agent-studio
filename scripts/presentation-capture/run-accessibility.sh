#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
stack="${repo_root}/scripts/worktree-test-stack.sh"
capture="${1:-after}"
if [[ "$#" -gt 0 ]]; then shift; fi
if [[ "${capture}" != "before" && "${capture}" != "after" ]]; then
  echo "Usage: $0 [before|after]" >&2
  exit 2
fi

cleanup() { bash "${stack}" down >/dev/null 2>&1 || true; }
trap cleanup EXIT

bash "${stack}" down >/dev/null 2>&1 || true
ATP_TEST_DEMO_PROFILE=local bash "${stack}" up --demo --with-frontend
eval "$(bash "${stack}" env)"
cd "${repo_root}/frontend"
PW_A11Y_PINNED_DEMO=1 A11Y_RAIL_CAPTURE="${capture}" \
  PW_BASE_URL="${PW_BASE_URL}" PW_BACKEND_URL="${PW_BACKEND_URL}" \
  node ./node_modules/@playwright/test/cli.js test \
    e2e/visual-evidence/accessibility-pinned-demo.spec.ts --project=visual-evidence "$@"
