#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
capture="${1:-after}"
if [[ "$#" -gt 0 ]]; then shift; fi
if [[ "${capture}" != "before" && "${capture}" != "after" ]]; then
  echo "Usage: $0 [before|after]" >&2
  exit 2
fi

ATP_TEST_DEMO_PROFILE=local PW_E2E_PROFILE=demo PW_A11Y_PINNED_DEMO=1 \
  PW_E2E_DOTNET_CONFIGURATION=Debug PW_E2E_PUBLISH_NO_BUILD=1 \
  A11Y_RAIL_CAPTURE="${capture}" \
  npm --prefix "${repo_root}/frontend" run e2e -- \
    e2e/visual-evidence/accessibility-pinned-demo.spec.ts --project=visual-evidence "$@"
