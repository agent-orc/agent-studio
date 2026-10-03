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
for tool in dotnet curl jq git sha256sum bc; do command -v "$tool" >/dev/null || { echo "missing tool: $tool" >&2; exit 64; }; done

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
origin="$root/origin.git"; work="$root/work"
git init --bare -b main "$origin" >/dev/null
git init -b main "$work" >/dev/null
printf 'Recovery drill repository\n' >"$work/README.md"
git -C "$work" add README.md
git -C "$work" -c user.name=drill -c user.email=drill@example.invalid commit -m 'seed recovery origin' >/dev/null
git -C "$work" push "$origin" main >/dev/null
jq -n --arg origin "$origin" '{installationId:"inst_drill",repositories:[{repositoryId:"recovery-repo",origin:$origin,refs:["refs/heads/main"]}]}' >"$root/custody.json"

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
backup="$(STORE_PATH="$source_dir" ts recovery capture --custody "$root/custody.json" | jq -r .manifest.dataSet.backupId)"
STORE_PATH="$source_dir" ts recovery copy --backup "$backup" --to "$offhost" >"$root/copy.json"
capture_seconds="$(echo "$(now) - $capture_started" | bc)"

# 3. One write after capture, then the loss instant.
serve "$source_dir"
api -X POST "$url/api/v1/projects/$project/tasks" -d '{"title":"after capture","state":"1-backlog"}' >/dev/null
source_tasks="$(api "$url/api/v1/projects/$project/tasks" | jq -c '[.[].taskKey] | sort')"
stop
loss_at="$(iso)"; loss_epoch="$(now)"

# 4. Verify the copy and restore onto an empty target.
ts recovery verify --from "$offhost/$backup" >"$root/verify.json"
STORE_PATH="$target_dir" ts recovery restore --from "$offhost/$backup" --loss-at "$loss_at" >"$root/restore.json"
STORE_PATH="$target_dir" ts recovery resume --check-only >"$root/gate-before.json" || true
STORE_PATH="$target_dir" ts recovery fence-hosts >/dev/null
STORE_PATH="$target_dir" ts recovery resume --old-writer-closed >"$root/resume.json"

# 5. Reconnect a disposable host, publish a Git ref, and complete its canary run.
serve "$target_dir"
restored_tasks="$(api "$url/api/v1/projects/$project/tasks" | jq -c '[.[].taskKey] | sort')"
api -X PUT "$url/api/v1/runners/runner-recovery" -d '{"name":"runner-recovery","hostId":"host-recovery","instanceId":"runner-recovery:2","runnerVersion":"1.0.0","protocolVersion":2,"capabilities":["coding-executor"]}' >/dev/null
canary_task="$(api -X POST "$url/api/v1/projects/$project/tasks" -d '{"title":"recovery canary","state":"2-ready"}')"
claim="$(api -X POST "$url/api/v1/runners/runner-recovery/claims" -d '{"runnerId":"runner-recovery","instanceId":"runner-recovery:2"}')"
[[ "$(jq -r .task.taskId <<<"$claim")" == "$(jq -r .taskId <<<"$canary_task")" ]] || { echo 'canary claim did not select the canary task' >&2; exit 1; }
run_id="$(jq -r .run.runId <<<"$claim")"
lease_id="$(jq -r .lease.leaseId <<<"$claim")"
fence="$(jq -r .lease.fence <<<"$claim")"
base_sha="$(git -C "$work" rev-parse HEAD)"
printf 'Recovered authority completed a canary\n' >"$work/CANARY.md"
git -C "$work" add CANARY.md
git -C "$work" -c user.name=drill -c user.email=drill@example.invalid commit -m 'recovery canary' >/dev/null
result_sha="$(git -C "$work" rev-parse HEAD)"
result_ref="refs/heads/agent-studio/results/$run_id/fence-$fence/$result_sha"
git -C "$work" push "$origin" "HEAD:$result_ref" >/dev/null
[[ "$(git ls-remote "$origin" "$result_ref" | cut -f1)" == "$result_sha" ]] || { echo 'canary ref did not reach origin' >&2; exit 1; }
artifact_digest="$(printf 'recovery canary\n' | sha256sum | cut -d' ' -f1)"
envelope="$(jq -nc --arg run "$run_id" --arg base "$base_sha" --arg result "$result_sha" --arg ref "$result_ref" --arg artifact "$artifact_digest" '{repositoryId:"recovery-repo",sourceRunAttemptId:$run,baseSha:$base,resultSha:$result,immutableRemoteRef:$ref,sourceBundleDigest:null,artifactManifestDigest:$artifact,submodules:[],lfsObjects:[],repositoryUrl:null}')"
envelope_digest="$(printf '%s' "$envelope" | sha256sum | cut -d' ' -f1)"
handoff="$(jq -nc --argjson envelope "$envelope" --arg digest "$envelope_digest" --arg run "$run_id" --arg lease "$lease_id" --argjson fence "$fence" '{runnerId:"runner-recovery",instanceId:"runner-recovery:2",leaseId:$lease,fence:$fence,sequence:1,idempotencyKey:("handoff:"+$run),envelopeDigest:$digest,envelope:$envelope}')"
api -X PUT "$url/api/v1/runs/$run_id/result-handoff" -d "$handoff" >/dev/null
completion="$(jq -nc --arg lease "$lease_id" --arg run "$run_id" --arg digest "$envelope_digest" --argjson fence "$fence" '{runnerId:"runner-recovery",instanceId:"runner-recovery:2",leaseId:$lease,fence:$fence,outcome:"success",summary:"Recovery canary published.",resultEnvelopeDigest:$digest,idempotencyKey:("completion:"+$run),sequence:2}')"
completed_canary="$(api -X POST "$url/api/v1/runs/$run_id/completion" -d "$completion")"
[[ "$(jq -r .status <<<"$completed_canary")" == success && "$(jq -r .resultSha <<<"$completed_canary")" == "$result_sha" && "$(jq -r .finishedAt <<<"$completed_canary")" != null ]] || { echo 'canary run did not finish successfully' >&2; exit 1; }
canary_epoch="$(now)"
stop

jq -n \
  --arg backup "$backup" \
  --arg lossAt "$loss_at" \
  --argjson captureSeconds "$capture_seconds" \
  --argjson rtoSeconds "$(echo "$canary_epoch - $loss_epoch" | bc)" \
  --argjson source "$source_tasks" \
  --argjson restored "$restored_tasks" \
  --argjson canary "$completed_canary" \
  --arg canaryRef "$result_ref" \
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
    measuredRecoveryTimeSecondsToCompletedCanary: $rtoSeconds,
    restoreSeconds: $restore[0].receipt.restoreSeconds,
    identityComparisons: [$restore[0].receipt.comparisons[] | {subject, matches}],
    verifyFindings: [$verify[0].findings[] | .code],
    gateBlockersBeforeFencing: [$gate[0].blockers[] | .code],
    resumed: $resume[0].resumed,
    sourceTasksAtLoss: $source,
    restoredTasks: $restored,
    lostAfterCapture: ($source - $restored),
    canary: {runId: $canary.runId, status: $canary.status, finishedAt: $canary.finishedAt, resultSha: $canary.resultSha, resultRef: $canaryRef},
    note: "Rehearsal measurement on one disposable host. A backup timer interval is not an achieved recovery point."
  }' | tee "$report"
