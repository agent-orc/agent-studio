#!/usr/bin/env bash
# Empty-target recovery rehearsal for the standalone Task Server (Dossier AGT-W63 D7 option A, I07).
#
# Seeds a disposable source authority through the HTTP API, captures a recovery set, copies it to a
# separate "off-host" directory, simulates the loss of the source, restores onto an empty data
# directory, fences hosts, passes the resume gate, and runs an API canary. Every directory lives under
# a fresh temporary root; no production store is opened. Times are wall clock and are reported as
# measured recovery point and time for this rehearsal only.
#
# Usage: scripts/recovery-drill.sh [report.json]
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
report="${1:-${JOB_RESULTS_DIR:-$repo}/recovery-drill-cli.json}"
for tool in dotnet curl jq; do command -v "$tool" >/dev/null || { echo "missing tool: $tool" >&2; exit 64; }; done

root="$(mktemp -d "${TMPDIR:-/tmp}/recovery-drill.XXXXXX")"
server_pid=""
cleanup() {
  [[ -n "$server_pid" ]] && kill "$server_pid" 2>/dev/null && wait "$server_pid" 2>/dev/null || true
  rm -rf "$root"
}
trap cleanup EXIT

dotnet build "$repo/task-server/TaskServer.csproj" -nologo -v q >/dev/null
bin="$repo/task-server/bin/Debug/net10.0/task-server.dll"
port="$(( 20000 + RANDOM % 20000 ))"
url="http://127.0.0.1:${port}"

ts() { dotnet "$bin" "$@"; }
now() { date -u +%s.%N; }
iso() { date -u +%Y-%m-%dT%H:%M:%S.%3NZ; }
api() { curl --fail --silent --show-error -H 'Content-Type: application/json' -H 'X-Task-Protocol-Version: 2' "$@"; }

serve() {
  STORE_PATH="$1" LISTEN_URL="$url" AUTH=none dotnet "$bin" >"$root/server-$(basename "$1").log" 2>&1 &
  server_pid=$!
  for _ in $(seq 1 120); do curl --silent --fail "$url/healthz" >/dev/null 2>&1 && return 0; sleep 0.25; done
  echo "server on $1 did not become healthy" >&2; cat "$root/server-$(basename "$1").log" >&2; exit 1
}
stop() { kill "$server_pid"; wait "$server_pid" 2>/dev/null || true; server_pid=""; }

source_dir="$root/source"; target_dir="$root/target"; offhost="$root/offhost"
mkdir -p "$offhost"

# 1. Disposable source authority with identities and tasks.
serve "$source_dir"
workspace="$(api -X POST "$url/api/v1/workspaces" -d '{"name":"Recovery drill"}' | jq -r .workspaceId)"
project="$(api -X POST "$url/api/v1/projects" -d "{\"workspaceId\":\"$workspace\",\"name\":\"Drill\",\"taskKeyPrefix\":\"DRL\"}" | jq -r .projectId)"
for title in one two three; do
  api -X POST "$url/api/v1/projects/$project/tasks" -d "{\"title\":\"$title\",\"state\":\"1-backlog\"}" >/dev/null
done
stop

# 2. Capture and off-host copy (the store is closed, so the command is the only writer).
capture_started="$(now)"
backup="$(STORE_PATH="$source_dir" ts recovery capture | jq -r .manifest.dataSet.backupId)"
STORE_PATH="$source_dir" ts recovery copy --backup "$backup" --to "$offhost" >"$root/copy.json"
capture_seconds="$(echo "$(now) - $capture_started" | bc)"

# 3. One write after capture, then the loss instant.
serve "$source_dir"
api -X POST "$url/api/v1/projects/$project/tasks" -d '{"title":"after capture","state":"1-backlog"}' >/dev/null
source_tasks="$(api "$url/api/v1/projects/$project/tasks" | jq -c '[.[].taskKey] | sort')"
stop
loss_at="$(iso)"; loss_epoch="$(now)"

# 4. Verify the copy and restore onto an empty target.
ts recovery verify --from "$offhost/$backup" --no-git >"$root/verify.json"
STORE_PATH="$target_dir" ts recovery restore --from "$offhost/$backup" --loss-at "$loss_at" >"$root/restore.json"
STORE_PATH="$target_dir" ts recovery resume --check-only >"$root/gate-before.json" || true
STORE_PATH="$target_dir" ts recovery fence-hosts >/dev/null
STORE_PATH="$target_dir" ts recovery resume --old-writer-closed >"$root/resume.json"

# 5. Serve the restored authority and finish a canary.
serve "$target_dir"
restored_tasks="$(api "$url/api/v1/projects/$project/tasks" | jq -c '[.[].taskKey] | sort')"
api -X POST "$url/api/v1/projects/$project/tasks" -d '{"title":"recovery canary","state":"1-backlog"}' >/dev/null
canary_epoch="$(now)"
stop

jq -n \
  --arg backup "$backup" \
  --arg lossAt "$loss_at" \
  --argjson captureSeconds "$capture_seconds" \
  --argjson rtoSeconds "$(echo "$canary_epoch - $loss_epoch" | bc)" \
  --argjson source "$source_tasks" \
  --argjson restored "$restored_tasks" \
  --slurpfile copy "$root/copy.json" \
  --slurpfile verify "$root/verify.json" \
  --slurpfile restore "$root/restore.json" \
  --slurpfile gate "$root/gate-before.json" \
  --slurpfile resume "$root/resume.json" \
  '{
    schema: "agent-studio.recovery-drill-cli/v1",
    backupId: $backup,
    setSha256: $copy[0].setSha256,
    lossAt: $lossAt,
    captureAndCopySeconds: $captureSeconds,
    measuredRecoveryPointSeconds: $restore[0].receipt.measuredRecoveryPointSeconds,
    measuredRecoveryTimeSecondsToCanary: $rtoSeconds,
    restoreSeconds: $restore[0].receipt.restoreSeconds,
    identityComparisons: [$restore[0].receipt.comparisons[] | {subject, matches}],
    verifyFindings: [$verify[0].findings[] | .code],
    gateBlockersBeforeFencing: [$gate[0].blockers[] | .code],
    resumed: $resume[0].resumed,
    sourceTasksAtLoss: $source,
    restoredTasks: $restored,
    lostAfterCapture: ($source - $restored),
    note: "Rehearsal measurement on one disposable host. A backup timer interval is not an achieved recovery point."
  }' | tee "$report"
