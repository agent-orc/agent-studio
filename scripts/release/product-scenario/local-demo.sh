#!/usr/bin/env bash
# Plays the product scenario once against a local demo installation: an
# isolated networked Studio on free loopback ports with its own workspace and a
# "Click Counter" project, the prebuilt frontend, and an Owner with a second
# factor. Nothing touches the shared workspace; everything is removed on exit.
#
# The local installation has no deploy host, so Click Counter is not live on
# stage and production. The run records that as the failed setup step "live"
# and still plays the invitations, the refusals and the teardown for real.
#
# Prerequisites: `dotnet build agent-taskboard.sln` and
# `bash scripts/npm-frontend.sh run build` in this checkout.
# Usage: scripts/release/product-scenario/local-demo.sh [--teardown-only-report DIR] [scenario options]
#   --teardown-only-report DIR  afterwards run `--teardown only` on the same
#                               installation and write its report to DIR
#                               (pair it with `--teardown never`)
set -euo pipefail

teardown_report=''
if [[ "${1:-}" == --teardown-only-report ]]; then
  teardown_report="${2:?--teardown-only-report needs a directory}"
  shift 2
fi

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
SCENARIO_DIR="${REPO_ROOT}/scripts/release/product-scenario"
FRONTEND_DIST="${REPO_ROOT}/frontend/dist/frontend/browser"
[[ -f "${FRONTEND_DIST}/index.html" ]] || { echo "Build the frontend first: bash scripts/npm-frontend.sh run build" >&2; exit 2; }

read -r backend_port frontend_port <<< "$(node "${REPO_ROOT}/scripts/find-free-port.mjs" --count 2)"
workspace="$(mktemp -d "${TMPDIR:-/tmp}/product-scenario-demo-XXXXXX")"
frontend_pid=''
backend_started=0
cleanup() {
  [[ -n "${frontend_pid}" ]] && kill "${frontend_pid}" 2>/dev/null || true
  if (( backend_started )); then PORT="${backend_port}" API_PORT_OVERRIDE=1 bash "${REPO_ROOT}/api.sh" stop >/dev/null || true; fi
  case "${workspace}" in */product-scenario-demo-*) rm -rf "${workspace}" ;; esac
}
trap cleanup EXIT

# Click Counter: the small website the scenario evolves.
site="${workspace}/click-counter"
mkdir -p "${site}" "${workspace}/projects/click-counter" "${workspace}/config"
cat > "${site}/index.html" <<'HTML'
<!doctype html><html lang="en"><head><meta charset="utf-8"><title>Click Counter</title></head>
<body><h1>Click Counter</h1><p id="count">0</p>
<button id="increment" type="button">Count</button> <button id="reset" type="button">Reset</button>
<script>
let count = 0; const view = document.getElementById('count');
document.getElementById('increment').onclick = () => { view.textContent = String(++count); };
document.getElementById('reset').onclick = () => { count = 0; view.textContent = '0'; };
</script></body></html>
HTML
git -C "${site}" init -q -b main
git -C "${site}" -c user.name='Product Scenario' -c user.email='product-scenario@example.invalid' add .
git -C "${site}" -c user.name='Product Scenario' -c user.email='product-scenario@example.invalid' commit -qm 'Click Counter'

# The installer code, generated per run and kept in a file of mode 600.
code_file="${workspace}/bootstrap-code"
(umask 077; node -e "process.stdout.write(require('node:crypto').randomBytes(24).toString('base64url'))" > "${code_file}")

# Set before the start: a start that outlives api.sh's health window still runs.
backend_started=1
(
  export PORT="${backend_port}" API_PORT_OVERRIDE=1 ATP_WORKTREE_TEST_BACKEND=1
  export TaskRepository="${workspace}" XDG_CONFIG_HOME="${workspace}/config" Runner__Role=test-subject
  export API_START_TIMEOUT_SECS="${API_START_TIMEOUT_SECS:-180}"
  export Security__Profile=networked Security__TrustedProxyAddresses=127.0.0.1
  Security__BootstrapCode="$(cat "${code_file}")"; export Security__BootstrapCode
  export WatchPaths__0__Name='Click Counter' WatchPaths__0__Path="${workspace}/projects/click-counter"
  export WatchPaths__0__RootPath="${site}" WatchPaths__0__RepositoryPath="${site}"
  bash "${REPO_ROOT}/api.sh" start
) || true
healthy=0
for _ in $(seq 1 "${BACKEND_HEALTH_WAIT_SECS:-300}"); do
  if curl -fs -o /dev/null --max-time 2 "http://127.0.0.1:${backend_port}/healthz"; then healthy=1; break; fi
  sleep 1
done
(( healthy )) || { echo "The local Studio did not become healthy on :${backend_port}." >&2; exit 1; }

# The edge: the prebuilt frontend over HTTPS with a per-run certificate for
# localhost, keeping the Host and forwarding the scheme like the networked edge
# (the public-demo mode of serve-prebuilt-frontend.mjs), so Origin checks and
# __Host- cookies behave as on an installation.
export PW_E2E_TLS_CERT="${workspace}/localhost.crt" PW_E2E_TLS_KEY="${workspace}/localhost.key"
openssl req -x509 -newkey rsa:2048 -sha256 -nodes -days 1 -subj '/CN=localhost' \
  -addext 'subjectAltName=DNS:localhost' -keyout "${PW_E2E_TLS_KEY}" -out "${PW_E2E_TLS_CERT}" >/dev/null 2>&1
export NODE_EXTRA_CA_CERTS="${PW_E2E_TLS_CERT}"
PW_E2E_PROFILE=public-demo node "${REPO_ROOT}/frontend/scripts/serve-prebuilt-frontend.mjs" \
  "${FRONTEND_DIST}" "${frontend_port}" "http://127.0.0.1:${backend_port}" > "${workspace}/frontend.log" 2>&1 &
frontend_pid=$!
studio="https://localhost:${frontend_port}"
for _ in $(seq 1 30); do
  curl -fs -o /dev/null --max-time 2 --cacert "${PW_E2E_TLS_CERT}" "${studio}/" && break
  sleep 1
done

node "${SCENARIO_DIR}/local-owner.mjs" --studio-url "${studio}" --bootstrap-code-file "${code_file}" --out "${workspace}/owner.json"
set +e
node "${REPO_ROOT}/scripts/release/product-scenario.mjs" --studio-url "${studio}" --user-file "${workspace}/owner.json" \
  --platform local-demo --poll-seconds 2 "$@"
status=$?
if [[ -n "${teardown_report}" ]]; then
  node "${REPO_ROOT}/scripts/release/product-scenario.mjs" --studio-url "${studio}" --user-file "${workspace}/owner.json" \
    --platform local-demo --poll-seconds 2 --teardown only --report-dir "${teardown_report}"
  teardown_status=$?
  echo "teardown-only exit code: ${teardown_status}"
  (( status != 0 )) || status="${teardown_status}"
fi
set -e
exit "${status}"
