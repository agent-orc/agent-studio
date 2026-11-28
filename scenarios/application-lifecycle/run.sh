#!/usr/bin/env bash
# Run from a prepared checkout. Both scenarios reuse the same built assets.
set -euo pipefail
cd "$(dirname "$0")/../.."
case "${1:-}" in lifecycle|product) ;; *) echo 'Usage: scenarios/application-lifecycle/run.sh lifecycle|product' >&2; exit 2 ;; esac
export JOB_RESULTS_DIR="${JOB_RESULTS_DIR:-$PWD/artifacts/scenarios}"
mkdir -p "$JOB_RESULTS_DIR"
# The two commands publish one combined report and may share its directory.
exec 9<"$JOB_RESULTS_DIR"
flock 9
scratch=""
scenario="$1"
phase="preparing the run"
finish() {
  local status=$?
  if [[ -n "$scratch" ]]; then rm -rf "$scratch"; fi
  node scenarios/application-lifecycle/report.mjs "$scenario" finish "$status" "$phase"
  exit "$status"
}
node scenarios/application-lifecycle/report.mjs "$scenario" start
trap finish EXIT
scratch="$(mktemp -d)"
phase="building the frontend"
if [[ ! -f frontend/dist/frontend/browser/index.html ]]; then
  bash scripts/npm-frontend.sh run build 9<&-
fi
phase="building the solution"
if [[ ! -f runner/bin/Debug/net10.0/agent-host.dll || ! -f backend/bin/Debug/net10.0/OrchestratorApi.dll ]]; then
  dotnet build agent-taskboard.sln --no-restore -m:2 9<&-
fi
export Deploy__ReleaseEvidenceRoot="${scratch}/release-evidence"
# The scenario exercises its own new project alongside the startup recovery records.
export DOTNET_PROCESSOR_COUNT="${DOTNET_PROCESSOR_COUNT:-2}"
export PRODUCT_START_DEADLINE_SECS="${PRODUCT_START_DEADLINE_SECS:-300}"
export PRODUCT_SCENARIO_REQUEST_TIMEOUT_MS="${PRODUCT_SCENARIO_REQUEST_TIMEOUT_MS:-120000}"
export PRODUCT_START_FIXTURE_CARDS="${PRODUCT_START_FIXTURE_CARDS:-5}"
phase="starting the fixture and running the browser scenario"
bash scripts/product-start-fixture-check.sh bash scenarios/application-lifecycle/serve.sh "$1" 9<&-
