#!/usr/bin/env bash
# One measured run for the API stall investigation (AGT-3369).
#
# Boots a published API on a workspace from generate-workspace.mjs, then for
# the given duration collects:
#   counters.csv     dotnet-counters System.Runtime (heap, GC pauses, pool threads, queue)
#   threads.csv      OS thread count and RSS of the API process every 5 s
#   stacks-*.txt     dotnet-stack snapshots every 5 minutes (what pool threads wait on)
#   heap-*.gcdump    dotnet-gcdump at 10 minutes and at the end (live heap by type)
#   probe.csv, drive-summary.json   from drive.mjs
#   api.log          API stdout and stderr
#
# Usage:
#   scripts/api-stall-load/run.sh <published-api-dir> <workspace-root> <out-dir> [duration-seconds] [port]
# Needs dotnet-counters, dotnet-stack and dotnet-gcdump on PATH (or DIAG_TOOLS=<dir>).
# WORKSPACE_TEMPLATE=<dir> replaces <workspace-root> with a copy of that directory
# first, so before and after runs start from the same warmed state. Keep the
# template next to the workspace: agent worktrees record absolute paths.
set -euo pipefail

api_dir="$(cd "$1" && pwd)"
if [[ -n "${WORKSPACE_TEMPLATE:-}" ]]; then
  rm -rf "$2"
  cp -a "${WORKSPACE_TEMPLATE}" "$2"
fi
workspace="$(cd "$2" && pwd)"
out="$3"
duration="${4:-1800}"
port="${5:-5199}"
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/../.." && pwd)"
[[ -n "${DIAG_TOOLS:-}" ]] && export PATH="${DIAG_TOOLS}:${PATH}"
mkdir -p "${out}"
out="$(cd "${out}" && pwd)"

export PORT="${port}" API_PORT_OVERRIDE=1 ATP_WORKTREE_TEST_BACKEND=1
export TaskRepository="${workspace}"
export GIT_CONFIG_GLOBAL="${workspace}/gitconfig" XDG_CONFIG_HOME="${workspace}/config"
export Runner__Role=test-subject
export WatchPaths__0__Name='Load Project'
export WatchPaths__0__Path="${workspace}/projects/load-project"
export WatchPaths__0__RootPath="${workspace}/code"
export WatchPaths__0__RepositoryPath="${workspace}/code"
touch "${GIT_CONFIG_GLOBAL}"

# The API probes for agent CLIs on PATH and boots orchestrator sessions with
# them. The measured process gets a PATH without any agent CLI and a scratch
# HOME, so a load run never starts a model call or reads real credentials.
safe_bin="${workspace}/home/bin"
mkdir -p "${safe_bin}"
for tool in git dotnet sh bash env cat ls ps uname nproc ssh; do
  ln -sf "$(command -v "${tool}")" "${safe_bin}/${tool}"
done

# The API keeps some host state relative to its working directory; start it
# inside the scratch home so nothing lands in the checkout.
(cd "${workspace}/home" && exec env HOME="${workspace}/home" PATH="${safe_bin}" DOTNET_CLI_HOME="${workspace}/home" \
  dotnet "${api_dir}/OrchestratorApi.dll" --contentRoot "${repo_root}/backend" \
  --urls "http://127.0.0.1:${port}") > "${out}/api.log" 2>&1 &
api_pid=$!
helpers=()
cleanup() {
  for pid in "${helpers[@]}"; do kill "${pid}" 2>/dev/null || true; done
  kill "${api_pid}" 2>/dev/null || true
  wait "${api_pid}" 2>/dev/null || true
}
trap cleanup EXIT

for _ in $(seq 1 180); do
  curl -sf -o /dev/null "http://127.0.0.1:${port}/healthz" && break
  kill -0 "${api_pid}" 2>/dev/null || { echo "API exited during boot; see ${out}/api.log" >&2; exit 1; }
  sleep 1
done
echo "API pid ${api_pid} healthy on :${port}; measuring ${duration}s into ${out}"

dotnet-counters collect --process-id "${api_pid}" --format csv --refresh-interval 5 \
  --counters System.Runtime --output "${out}/counters.csv" > "${out}/counters.log" 2>&1 &
helpers+=($!)

(
  echo "elapsed_s,threads,rss_kb"
  start=$(date +%s)
  while kill -0 "${api_pid}" 2>/dev/null; do
    echo "$(( $(date +%s) - start )),$(awk '/^Threads:/{print $2}' /proc/${api_pid}/status),$(awk '/^VmRSS:/{print $2}' /proc/${api_pid}/status)"
    sleep 5
  done
) > "${out}/threads.csv" &
helpers+=($!)

(
  i=0
  while kill -0 "${api_pid}" 2>/dev/null; do
    sleep 300
    dotnet-stack report --process-id "${api_pid}" > "${out}/stacks-$(( ++i * 5 ))min.txt" 2>&1 || true
    if (( i == 2 )); then dotnet-gcdump collect --timeout 300 --process-id "${api_pid}" --output "${out}/heap-10min.gcdump" > /dev/null 2>&1 || true; fi
  done
) &
helpers+=($!)

node "${script_dir}/drive.mjs" --base-url "http://127.0.0.1:${port}" --workspace "${workspace}" \
  --out "${out}" --duration-seconds "${duration}"

dotnet-gcdump collect --timeout 300 --process-id "${api_pid}" --output "${out}/heap-end.gcdump" > /dev/null 2>&1 || true
dotnet-gcdump report "${out}/heap-end.gcdump" > "${out}/heap-end.txt" 2>&1 || true
[[ -f "${out}/heap-10min.gcdump" ]] && { dotnet-gcdump report "${out}/heap-10min.gcdump" > "${out}/heap-10min.txt" 2>&1 || true; }
