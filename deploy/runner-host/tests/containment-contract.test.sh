#!/usr/bin/env bash
set -euo pipefail

runner_host_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
repo_dir="$(cd -- "$runner_host_dir/../.." && pwd)"
script="$runner_host_dir/apply-containment.sh"

assert_values() {
  local actual expected
  actual=$("$script" --calculate "$1" "$2")
  expected=$3
  if [[ $actual != "$expected" ]]; then
    printf 'Unexpected values for %s cores / %s kB:\n%s\nExpected:\n%s\n' "$1" "$2" "$actual" "$expected" >&2
    exit 1
  fi
}

assert_values 12 $((62 * 1024 * 1024)) \
  $'CPUQuota=1000%\nMemoryLow=10240M\nMemoryHigh=47923M\nMemoryMax=53248M'
assert_values 2 $((4 * 1024 * 1024)) \
  $'CPUQuota=100%\nMemoryLow=1024M\nMemoryHigh=2764M\nMemoryMax=3072M'
assert_values 1 $((2 * 1024 * 1024)) \
  $'CPUQuota=100%\nMemoryLow=512M\nMemoryHigh=1382M\nMemoryMax=1536M'

if "$script" --calculate 1 1024 >/dev/null 2>&1; then
  echo 'Expected a host smaller than 2 GiB to fail.' >&2
  exit 1
fi

command -v docker >/dev/null || { echo 'docker compose is required for this contract test.' >&2; exit 1; }
export STUDIO_VERSION=0.0.0 STUDIO_DOMAIN=studio.example.test
export STUDIO_DATA_DIR=/tmp/studio-contract STUDIO_BOOTSTRAP_CODE=contract-test-code
export ENGINE_VERSION=0.0.0 ENGINE_SERVER_URL=https://studio.example.test
export ENGINE_CLIENT_ID=engine_contract ENGINE_CREDENTIAL_FILE=/tmp/engine-contract-token

docker compose \
  -f "$repo_dir/deploy/compose/monolith-networked/compose.yaml" \
  -f "$repo_dir/deploy/compose/monolith-networked/compose.runner-host.yaml" \
  config --format json |
  python3 -c 'import json, sys
services = json.load(sys.stdin)["services"]
assert set(services) == {"api", "frontend", "edge"}, services.keys()
assert all(service.get("cgroup_parent") == "studio.slice" for service in services.values()), services
assert int(services["api"]["mem_reservation"]) == 1073741824, services["api"]
assert int(services["api"]["mem_limit"]) == 4294967296, services["api"]
assert all("cpus" not in service and "cpu_quota" not in service for service in services.values()), services'

docker compose -f "$repo_dir/deploy/compose/monolith-engine/compose.yaml" config --format json |
  python3 -c 'import json, sys
services = json.load(sys.stdin)["services"]
assert set(services) == {"orchestrator-engine"}, services.keys()
assert all(service.get("cgroup_parent") == "studio.slice" for service in services.values()), services'

echo 'Runner-host containment contract passed.'
