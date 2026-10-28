#!/usr/bin/env bash
# DP-S3 acceptance proof for the Linux work container executor on a real
# Docker host. Builds the role image, publishes it to a loopback registry so the
# executor pulls it by digest, materializes this repository from a bare mirror,
# and runs `agent-host work-container proof`, which records over-budget
# denial, the exact-SHA guard, restart reconciliation, N container attempts
# with verified cleanup, and the same N attempts through the process executor.
#
# Usage: scripts/work-container-proof.sh <report-dir> [attempts] [parallel]
set -euo pipefail

report="${1:?report directory required}"
attempts="${2:-100}"
parallel="${3:-4}"
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
runtime="${RUNNER_WORK_CONTAINER_RUNTIME:-docker}"
registry_name="agent-work-proof-registry"
registry_port="${WORK_CONTAINER_PROOF_REGISTRY_PORT:-5555}"
host_id="dp-s3-proof-$(hostname -s)"
work="$(mktemp -d)"
cleanup() {
  "$runtime" rm --force "$registry_name" >/dev/null 2>&1 || true
  rm -rf -- "$work"
}
trap cleanup EXIT
mkdir -p "$report"

"$runtime" rm --force "$registry_name" >/dev/null 2>&1 || true
"$runtime" run --detach --name "$registry_name" --publish "127.0.0.1:${registry_port}:5000" registry:2 >/dev/null

local_tag="agent-work:dp-s3-proof"
"$runtime" build --quiet -f "$repo_root/deploy/work-container/Dockerfile" -t "$local_tag" \
  --build-arg SHA="$(git -C "$repo_root" rev-parse HEAD)" "$repo_root/deploy/work-container" >/dev/null
remote="127.0.0.1:${registry_port}/agent-work"
"$runtime" tag "$local_tag" "$remote:proof"
"$runtime" push --quiet "$remote:proof" >/dev/null
digest="$("$runtime" image inspect --format '{{range .RepoDigests}}{{println .}}{{end}}' "$remote:proof" | grep -F "$remote@" | head -n 1)"
[[ "$digest" =~ @sha256:[0-9a-f]{64}$ ]] || { echo "no registry digest for $remote" >&2; exit 1; }
# Remove every local reference so the executor resolves and pulls by digest. The
# build left the layers in this host's content store, so the proof records this
# pull as not cold (--image-layers-local); a fresh canary host records the cold pull.
"$runtime" rmi "$remote:proof" "$local_tag" >/dev/null
"$runtime" rmi "$digest" >/dev/null 2>&1 || true

sha="$(git -C "$repo_root" rev-parse HEAD)"
branch="$(git -C "$repo_root" rev-parse --abbrev-ref HEAD)"
git clone --quiet --bare --single-branch --branch "$branch" \
  "$(git -C "$repo_root" rev-parse --path-format=absolute --git-common-dir)" "$work/source.git"
chmod -R a+rX "$work"

manifest="$work/role-images.json"
dotnet "$repo_root/runner/bin/Debug/net10.0/agent-host.dll" work-container image-promote \
  --manifest "$manifest" --role all --image "$digest" --release "proof-${sha:0:9}" >/dev/null
cp "$manifest" "$report/role-images.json"

status=0
RUNNER_WORK_CONTAINER_MODE=canary RUNNER_WORK_CONTAINER_RUNTIME="$runtime" \
  dotnet "$repo_root/runner/bin/Debug/net10.0/agent-host.dll" work-container proof \
  --host "$host_id" --image "$digest" --source "$work/source.git" --sha "$sha" \
  --repository-key agent-studio --attempts "$attempts" --parallel "$parallel" \
  --state-dir "$work/state" --report "$report" --image-layers-local true \
  | tee "$report/proof.log" || status=$?

# Independent check, outside the executor's own bookkeeping.
live="$("$runtime" ps --all --quiet --filter "label=agent-studio.host=$host_id" | wc -l)"
volumes="$("$runtime" volume ls --quiet --filter "label=agent-studio.host=$host_id" | wc -l)"
printf 'independent check: containers=%s attempt-volumes=%s\n' "$live" "$volumes" | tee -a "$report/proof.log"
cp -r "$work/state/receipts" "$report/receipts"
"$runtime" volume ls --quiet | grep -E '^agent-cache-(npm|nuget|repo)-v[0-9]{4,}$' | xargs -r "$runtime" volume rm >/dev/null
(( live == 0 && volumes == 0 )) || status=1
exit "$status"
