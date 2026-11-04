#!/usr/bin/env bash
# SM-11 containment probe on a real agent host (security-model Dossier).
# Runs one probe attempt through the work-container executor, exactly as the
# host agent runs work: contained mode, the narrow launcher, the work-zone
# network and a fresh attempt UID. The attempt materializes this repository at
# HEAD and then tries to read the named host paths (the runner credential,
# provider logins, another project's checkout), find a runtime socket or a
# secret-shaped variable, see another repository's mirror, and connect to the
# blocked addresses. The script exits 0 only when every check passed and the
# attempt left no container or volume behind.
#
# Run as the operator (root), after scripts/harden-agent-runner-host.sh --apply
# and agent-host zone-policy apply --execute:
#
#   scripts/work-container-containment-probe.sh <report-dir> <host-id> \
#     <forbidden-path,...> <blocked-host:port,...> [role-image-manifest]
#
# Use a documentation address (192.0.2.0/24) or another address outside the
# egress allowlist for the blocked probe; name no real hosts in shared reports.
set -euo pipefail

report="${1:?report directory required}"
host_id="${2:?host id required}"
forbidden="${3:?comma-separated forbidden paths required}"
blocked="${4:?comma-separated blocked host:port list required}"
manifest="${5:-/etc/agent-host/work-container-images.json}"
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
agent_host="${AGENT_HOST_DLL:-$repo_root/runner/bin/Debug/net10.0/agent-host.dll}"
work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT
mkdir -p "$report"

sha="$(git -C "$repo_root" rev-parse HEAD)"
branch="$(git -C "$repo_root" rev-parse --abbrev-ref HEAD)"
git clone --quiet --bare --single-branch --branch "$branch" \
  "$(git -C "$repo_root" rev-parse --path-format=absolute --git-common-dir)" "$work/source.git"
chmod -R a+rX "$work"

status=0
RUNNER_WORK_CONTAINER_MODE=contained RUNNER_WORK_CONTAINER_IMAGES="$manifest" \
  dotnet "$agent_host" work-container check | tee "$report/check.json" || true
RUNNER_WORK_CONTAINER_MODE=contained RUNNER_WORK_CONTAINER_IMAGES="$manifest" \
  dotnet "$agent_host" work-container probe \
  --host "$host_id" --repository-key agent-studio --source "$work/source.git" --sha "$sha" \
  --forbidden-paths "$forbidden" --blocked-addresses "$blocked" \
  --state-dir "$work/state" --report "$report" | tee "$report/probe.log" || status=$?
cp -r "$work/state/receipts" "$report/receipts" 2>/dev/null || true
exit "$status"
