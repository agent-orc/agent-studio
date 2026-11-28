#!/usr/bin/env bash
# AGT-4239: start the built product with the production start path (hosted
# services on, repair sweeps on, networked server profile, Production
# environment) on the production-shaped workspace of scripts/startup-fixture.sh
# and require /healthz within a deadline. On failure it names the start-up
# phase the product is stuck in (the startup-phase-slow watchdog line, else the
# last finished phase), prints a managed stack when dotnet-stack is available,
# and exits 1. The product process is always stopped.
#
# Optional arguments are a command to run against the healthy fixture. It receives
# PRODUCT_FIXTURE_WORKSPACE and PRODUCT_FIXTURE_URL, and its exit code is propagated.
#
# Environment:
#   PRODUCT_START_DLL_DIR       directory with OrchestratorApi.dll
#                               (default backend/bin/<configuration>/net10.0)
#   PRODUCT_START_DEADLINE_SECS seconds until /healthz must answer (default 90)
#   PRODUCT_START_FIXTURE_CARDS cards in the fixture (default 2000)
#   PRODUCT_START_LOG           where to keep the product log (default: temp)
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
configuration="${PW_E2E_DOTNET_CONFIGURATION:-Debug}"
dll_dir="${PRODUCT_START_DLL_DIR:-${repo_root}/backend/bin/${configuration}/net10.0}"
deadline="${PRODUCT_START_DEADLINE_SECS:-90}"
cards="${PRODUCT_START_FIXTURE_CARDS:-2000}"

if [[ ! -f "${dll_dir}/OrchestratorApi.dll" ]]; then
  echo "product-start-fixture: no OrchestratorApi.dll in ${dll_dir}; building the backend once" >&2
  dotnet build "${repo_root}/backend/OrchestratorApi.csproj" --configuration "${configuration}" -v q
fi

workspace="$(mktemp -d "${TMPDIR:-/tmp}/product-start-fixture-XXXXXX")"
keys="$(mktemp -d "${TMPDIR:-/tmp}/product-fixture-keys-XXXXXX")"
log="${PRODUCT_START_LOG:-${workspace}.log}"
pid=""
stop() {
  if [[ -n "${pid}" ]] && kill -0 "${pid}" 2>/dev/null; then
    kill "${pid}" 2>/dev/null || true
    for _ in $(seq 1 20); do kill -0 "${pid}" 2>/dev/null || break; sleep 0.5; done
    kill -9 "${pid}" 2>/dev/null || true
  fi
  rm -rf "${workspace}" "${keys}"
}
trap stop EXIT

bash "${repo_root}/scripts/startup-fixture.sh" "${workspace}" "${cards}"
port="$(node "${repo_root}/scripts/find-free-port.mjs" --count 1)"

# Keep every provider and home lookup inside the disposable workspace.
mkdir -p "${workspace}/home" "${workspace}/bin" "${workspace}/config" "${workspace}/data" "${workspace}/cache"
for provider in codex claude copilot gemini antigravity; do
  printf '#!/bin/sh\n[ "$1" = --version ] && echo "%s 0.0.0" && exit 0\nexit 69\n' "${provider}" > "${workspace}/bin/${provider}"
  chmod 700 "${workspace}/bin/${provider}"
done

(
  cd "${dll_dir}"
  export PATH="${workspace}/bin:${PATH}" HOME="${workspace}/home" GIT_CONFIG_GLOBAL="${workspace}/gitconfig"
  export XDG_CONFIG_HOME="${workspace}/config" XDG_DATA_HOME="${workspace}/data" XDG_CACHE_HOME="${workspace}/cache"
  export CodexCli__Path="${workspace}/bin/codex" ClaudeCli__Path="${workspace}/bin/claude" \
    CopilotCli__Path="${workspace}/bin/copilot" GeminiCli__Path="${workspace}/bin/gemini" \
    AntigravityCli__Path="${workspace}/bin/antigravity"
  export ASPNETCORE_ENVIRONMENT=Production TaskRepository="${workspace}" AllowedHosts='*'
  export Security__Profile=networked Security__TrustedProxyAddresses=127.0.0.1
  export Security__BootstrapCode="${workspace##*/}-owner-code"
  export Secrets__KeyFile="${keys}/studio-secrets.kek"
  export RemoteGate__SshHost=gate-startup-fixture.invalid RemoteGate__WorkerPath=/opt/agentstudio-startup-fixture/worker
  export WatchPaths__0__Name=fixture WatchPaths__0__Path="${workspace}/projects/fixture" \
    WatchPaths__0__RootPath="${workspace}/repo" WatchPaths__0__RepositoryPath="${workspace}/repo"
  exec dotnet OrchestratorApi.dll --urls "http://127.0.0.1:${port}"
) > "${log}" 2>&1 &
pid=$!

started="$(date +%s)"
healthy=0
while (( $(date +%s) - started < deadline )); do
  if ! kill -0 "${pid}" 2>/dev/null; then
    echo "product-start-fixture: the product exited during start-up" >&2
    break
  fi
  if [[ "$(curl -s -m 2 -o /dev/null -w '%{http_code}' "http://127.0.0.1:${port}/healthz" || true)" == 200 ]]; then
    healthy=1
    break
  fi
  sleep 1
done
elapsed=$(( $(date +%s) - started ))

if (( healthy )); then
  echo "product-start-fixture: healthy after ${elapsed}s (deadline ${deadline}s, ${cards} cards)"
  # The review gate reads a test step as evidence only when it names a
  # completed test count; this check is one test (AGT-4239 review finding).
  echo "product-start-fixture: Total: 1, Passed: 1, Failed: 0"
  grep -E 'startup-phase(-slow)? |startup-repair' "${log}" | sed 's/^/  /' | tail -60 || true
  [[ -n "${PRODUCT_START_LOG:-}" ]] || rm -f "${log}"
  if (( $# )); then
    PRODUCT_FIXTURE_WORKSPACE="${workspace}" PRODUCT_FIXTURE_URL="http://127.0.0.1:${port}" "$@"
  fi
  exit 0
fi

slow="$(grep -E 'startup-phase-slow phase=' "${log}" | tail -1 || true)"
last="$(grep -E 'startup-phase phase=' "${log}" | tail -1 || true)"
if [[ -n "${slow}" ]]; then
  phase="$(sed -E 's/.*startup-phase-slow phase=([^ ]+).*/\1/' <<< "${slow}")"
  echo "product-start-fixture: NOT healthy after ${elapsed}s; stuck in start-up phase ${phase}" >&2
  echo "  ${slow}" >&2
elif [[ -n "${last}" ]]; then
  phase="$(sed -E 's/.*startup-phase phase=([^ ]+).*/\1/' <<< "${last}")"
  echo "product-start-fixture: NOT healthy after ${elapsed}s; stuck in the phase after ${phase}" >&2
else
  echo "product-start-fixture: NOT healthy after ${elapsed}s; the product logged no start-up phase (a build without the AGT-4239 phase log)" >&2
fi
if command -v dotnet-stack >/dev/null 2>&1 && kill -0 "${pid}" 2>/dev/null; then
  echo "product-start-fixture: main thread stack:" >&2
  timeout 60 dotnet-stack report -p "${pid}" 2>&1 \
    | awk '/^Thread \(/ { if (block ~ /Program\.<Main>/) printf "%s", block; block = "" } { block = block $0 "\n" }
           END { if (block ~ /Program\.<Main>/) printf "%s", block }' \
    | grep -E '^Thread|OrchestratorApi!' | head -40 >&2 || true
fi
echo "product-start-fixture: last log lines (${log}):" >&2
tail -25 "${log}" | cut -c1-400 >&2
exit 1
