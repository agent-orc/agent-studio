#!/usr/bin/env bash
# Build once per checkout, then launch a short-lived isolated Playwright stack.
set -euo pipefail

FRONTEND_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REPO_ROOT="$(cd "${FRONTEND_ROOT}/.." && pwd)"
if [[ "${PW_E2E_SINGLE_RUN:-0}" != "1" && "$#" -eq 0 ]]; then
  result=0
  for profile in standard recovery ref-drift demo public-demo first-run networked-monolith networked-https networked-checkout networked-https-demo operator-cli; do
    PW_E2E_SINGLE_RUN=1 PW_E2E_PROFILE="${profile}" bash "$0" || result=1
  done
  exit "${result}"
fi
if [[ -z "${PW_E2E_PROFILE:-}" ]]; then
  PW_E2E_PROFILE=standard
  for arg in "$@"; do
    case "${arg}" in
      *crash-recovery-prompt.spec.ts*|*first-run-checklist.spec.ts*) PW_E2E_PROFILE=recovery ;;
      *first-run-journey.spec.ts*|*first-run-review-host-restart.spec.ts*) PW_E2E_PROFILE=first-run ;;
      *first-run-networked-monolith.spec.ts*) PW_E2E_PROFILE=networked-monolith ;;
      *product-session-networked-https.spec.ts*) PW_E2E_PROFILE=networked-https ;;
      *reviewer-role.networked-https.spec.ts*) PW_E2E_PROFILE=networked-https ;;
      *owner-second-factor-human-pace.spec.ts*) PW_E2E_PROFILE=networked-https ;;
      *networked-project-checkout.spec.ts*) PW_E2E_PROFILE=networked-checkout ;;
      *product-card-detail.spec.ts*) PW_E2E_PROFILE=networked-https-demo ;;
      *studio-op-operator-cli.spec.ts*) PW_E2E_PROFILE=operator-cli ;;
      *cockpit-integration-matrix.spec.ts*) PW_E2E_PROFILE=demo ;;
      *dossier-decision-live.spec.ts*) PW_E2E_PROFILE=demo ;;
      *public-demo-shell.spec.ts*) PW_E2E_PROFILE=public-demo ;;
    esac
  done
fi
PUBLISH_DIR="${REPO_ROOT}/backend/bin/e2e-publish"
PUBLISH_CONFIGURATION="${PW_E2E_DOTNET_CONFIGURATION:-Release}"
if [[ "${PW_E2E_PROFILE}" == public-demo ]]; then
  # Keep the browser-test Debug output separate from the Release publish.
  PUBLISH_DIR="${REPO_ROOT}/backend/bin/e2e-publish-public-demo"
  PUBLISH_CONFIGURATION=Debug
fi
PUBLISH_BUILD_ARGS=()
if [[ "${PW_E2E_PUBLISH_NO_BUILD:-0}" == "1" ]]; then
  PUBLISH_BUILD_ARGS+=(--no-build)
fi
CHECKOUT_SHA="$(bash "${REPO_ROOT}/scripts/e2e-source-fingerprint.sh")"

if [[ ! -f "${PUBLISH_DIR}/OrchestratorApi.dll" \
   || "$(cat "${PUBLISH_DIR}/E2E-FINGERPRINT" 2>/dev/null || true)" != "${CHECKOUT_SHA}" \
   || "$(cat "${PUBLISH_DIR}/E2E-CONFIGURATION" 2>/dev/null || true)" != "${PUBLISH_CONFIGURATION}" ]]; then
  dotnet publish "${REPO_ROOT}/backend/OrchestratorApi.csproj" --configuration "${PUBLISH_CONFIGURATION}" \
    --output "${PUBLISH_DIR}" "${PUBLISH_BUILD_ARGS[@]}"
  git -C "${REPO_ROOT}" rev-parse HEAD > "${PUBLISH_DIR}/RELEASE-SHA"
  printf '%s\n' "${CHECKOUT_SHA}" > "${PUBLISH_DIR}/E2E-FINGERPRINT"
  printf '%s\n' "${PUBLISH_CONFIGURATION}" > "${PUBLISH_DIR}/E2E-CONFIGURATION"
fi
if [[ "${PW_E2E_PROFILE}" == first-run || "${PW_E2E_PROFILE}" == networked-checkout ]]; then
  dotnet publish "${REPO_ROOT}/runner/AgentRunner.csproj" --configuration Release \
    --output "${REPO_ROOT}/runner/bin/e2e-publish"
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
# public-demo and networked-https serve the browser over real TLS, so Secure
# session cookies behave as on a deployed Studio.
if [[ "${PW_E2E_PROFILE}" == public-demo || "${PW_E2E_PROFILE}" == networked-https || "${PW_E2E_PROFILE}" == networked-checkout || "${PW_E2E_PROFILE}" == networked-https-demo ]]; then
  PW_E2E_TLS_CERT="${PW_E2E_WORKSPACE}/localhost.crt"
  PW_E2E_TLS_KEY="${PW_E2E_WORKSPACE}/localhost.key"
  openssl req -x509 -newkey rsa:2048 -sha256 -nodes -days 1 \
    -subj '/CN=localhost' -addext 'subjectAltName=DNS:localhost' \
    -keyout "${PW_E2E_TLS_KEY}" -out "${PW_E2E_TLS_CERT}" >/dev/null 2>&1
  export PW_E2E_TLS_CERT PW_E2E_TLS_KEY
  export PW_BASE_URL="https://localhost:${PW_E2E_FRONTEND_PORT}"
else
  export PW_BASE_URL="http://127.0.0.1:${PW_E2E_FRONTEND_PORT}"
fi
export PW_BACKEND_URL="http://127.0.0.1:${PW_E2E_BACKEND_PORT}"
export DEV_PORT="${PW_E2E_BACKEND_PORT}"
export PW_PREBUILT=1
export PW_E2E_TEST_START_MS="$(node -p 'Date.now()')"

cd "${FRONTEND_ROOT}"
node ./node_modules/@playwright/test/cli.js test "$@"
