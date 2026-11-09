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

# The host install path is opt-in. A dedicated slice groups the service and
# every delegated worker cgroup, including a worker that outlives its daemon.
for service in agent-runner agent-runner-review agent-host; do
  grep -Fxq 'Slice=agent-work.slice' \
    "$runner_host_dir/systemd/$service.service.d/50-runner-containment.conf"
done
grep -Fq 'if ((shared_studio_host)); then' "$repo_dir/scripts/harden-agent-runner-host.sh"
grep -Fq 'apply-containment.sh' "$repo_dir/scripts/harden-agent-runner-host.sh"
if bash "$repo_dir/scripts/harden-agent-runner-host.sh" --apply --agent-cpu-weight 50 >/dev/null 2>&1; then
  echo 'Expected shared-host weights without opt-in to fail.' >&2
  exit 1
fi
default_slices=$("$script" --render-slices)
awk '
  /^# studio.slice$/ { slice = "studio" }
  /^# agent-work.slice$/ { slice = "agent" }
  /^CPUWeight=/ { cpu[slice] = $0 }
  /^IOWeight=/ { io[slice] = $0 }
  END { exit !(cpu["studio"] == "CPUWeight=100" && cpu["agent"] == "CPUWeight=50" &&
               io["studio"] == "IOWeight=100" && io["agent"] == "IOWeight=50") }
' <<<"$default_slices"
custom_slices=$("$script" --render-slices --agent-cpu-weight 40 --studio-cpu-weight 120 \
  --agent-io-weight 60 --studio-io-weight 200)
awk '
  /^# studio.slice$/ { slice = "studio" }
  /^# agent-work.slice$/ { slice = "agent" }
  /^CPUWeight=/ { cpu[slice] = $0 }
  /^IOWeight=/ { io[slice] = $0 }
  END { exit !(cpu["studio"] == "CPUWeight=120" && cpu["agent"] == "CPUWeight=40" &&
               io["studio"] == "IOWeight=200" && io["agent"] == "IOWeight=60") }
' <<<"$custom_slices"
if "$script" --render-slices --agent-cpu-weight 120 --studio-cpu-weight 100 >/dev/null 2>&1; then
  echo 'Expected an agent CPU weight at or above Studio to fail.' >&2
  exit 1
fi
if "$script" --render-slices --agent-io-weight 10001 >/dev/null 2>&1; then
  echo 'Expected an out-of-range IO weight to fail.' >&2
  exit 1
fi

# The read-only host probe reports effective weights and the worker hierarchy.
probe_bin=$(mktemp -d)
trap 'rm -rf "$probe_bin"' EXIT
cat >"$probe_bin/systemctl" <<'PROBE'
#!/usr/bin/env bash
case " $* " in
  *' --property=Slice --value '*) echo agent-work.slice ;;
  *' --property=ControlGroup --value '*) echo /agent-work.slice/agent-runner.service ;;
  *) printf 'CPUWeight=50\nIOWeight=50\n' ;;
esac
PROBE
cat >"$probe_bin/systemd-cgls" <<'PROBE'
#!/usr/bin/env bash
echo 'agent-work.slice/agent-runner.service/worker-detached'
PROBE
chmod +x "$probe_bin/systemctl" "$probe_bin/systemd-cgls"
probe_output=$(PATH="$probe_bin:$PATH" "$script" --check)
grep -Fq 'CPUWeight=50' <<<"$probe_output"
grep -Fq 'placement: slice=agent-work.slice cgroup=' <<<"$probe_output"
grep -Fq 'worker-detached' <<<"$probe_output"

command -v docker >/dev/null || { echo 'docker compose is required for this contract test.' >&2; exit 1; }
export STUDIO_VERSION=0.0.0 STUDIO_DOMAIN=studio.example.test
export STUDIO_DATA_DIR=/tmp/studio-contract STUDIO_BOOTSTRAP_CODE=contract-test-code
export STUDIO_SECRETS_KEY_FILE=/tmp/studio-contract-keys/secrets.kek STUDIO_PUBLIC_CLOUDFLARE_EDGE=false
export STUDIO_HOST_CA_DIR=/tmp/studio-contract-ca STUDIO_ACCESS_AUTH_URL=https://studio.example.test/auth
export STUDIO_API_DIGEST=sha256:contract-api STUDIO_WEB_DIGEST=sha256:contract-web
export STUDIO_EDGE_DIGEST=sha256:contract-edge
export STUDIO_OPERATIONS_AGENT_DIGEST=sha256:contract-agent
export STUDIO_OPERATIONS_SERVER_DIGEST=sha256:contract-server
export ENGINE_VERSION=0.0.0 ENGINE_SERVER_URL=https://studio.example.test
export ENGINE_CLIENT_ID=engine_contract ENGINE_CREDENTIAL_FILE=/tmp/engine-contract-token

docker compose \
  -f "$repo_dir/deploy/compose/monolith-networked/compose.yaml" \
  -f "$repo_dir/deploy/compose/monolith-networked/compose.runner-host.yaml" \
  config --format json |
  python3 -c 'import json, sys
services = json.load(sys.stdin)["services"]
assert {"api", "frontend", "edge"} <= set(services), services.keys()
assert all(services[name].get("cgroup_parent") == "studio.slice" for name in ("api", "frontend", "edge")), services
assert int(services["api"]["mem_reservation"]) == 1073741824, services["api"]
assert int(services["api"]["mem_limit"]) == 21474836480, services["api"]
assert all("cpus" not in service and "cpu_quota" not in service for service in services.values()), services'

docker compose -f "$repo_dir/deploy/compose/monolith-engine/compose.yaml" config --format json |
  python3 -c 'import json, sys
services = json.load(sys.stdin)["services"]
assert set(services) == {"orchestrator-engine"}, services.keys()
assert all(service.get("cgroup_parent") == "studio.slice" for service in services.values()), services'

echo 'Runner-host containment contract passed.'
