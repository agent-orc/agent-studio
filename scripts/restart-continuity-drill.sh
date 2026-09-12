#!/usr/bin/env bash
set -euo pipefail

if [[ "${1:-}" == "--check-readiness" ]]; then
  command -v curl >/dev/null
  command -v jq >/dev/null
  echo "restart-continuity-drill readiness: curl and jq available"
  echo "live target: intentionally deferred to the post-integration Windows Studio release gate"
  exit 0
fi

required=(STUDIO_URL UPDATE_URL LOCAL_PROJECT LOCAL_TASK_ID REMOTE_PROJECT REMOTE_TASK_ID)
for name in "${required[@]}"; do
  if [[ -z "${!name:-}" ]]; then
    echo "missing required environment variable: ${name}" >&2
    exit 64
  fi
done

POLL_SECONDS="${POLL_SECONDS:-2}"
TIMEOUT_SECONDS="${TIMEOUT_SECONDS:-900}"
CLIENT_ID="${CLIENT_ID:-restart-continuity-drill}"

studio="${STUDIO_URL%/}"
update="${UPDATE_URL%/}"
started_epoch="$(date +%s)"

headers=(-H "X-Client-Id: ${CLIENT_ID}")
update_headers=(-H 'Content-Type: application/json')
if [[ -n "${UPDATE_TOKEN:-}" ]]; then
  update_headers+=(-H "X-Update-Token: ${UPDATE_TOKEN}")
fi

encode() { jq -rn --arg value "$1" '$value|@uri'; }

detail() {
  local project="$1" task="$2"
  curl --fail --silent --show-error "${headers[@]}" \
    "${studio}/api/tasks/$(encode "$task")?project=$(encode "$project")"
}

timeline() {
  local project="$1" task="$2"
  curl --fail --silent --show-error "${headers[@]}" \
    "${studio}/api/tasks/$(encode "$task")/timeline?project=$(encode "$project")"
}

assert_in_flight() {
  local expected_kind="$1" project="$2" task="$3"
  local payload
  payload="$(detail "$project" "$task")"
  jq -e --arg expected "$expected_kind" '
    .info.state == "3-progress"
    and .info.executionLocation.executionKind == $expected
    and (.info.runActivity.kind == "active"
         or .info.runActivity.kind == "continuing-after-restart")
  ' <<<"$payload" >/dev/null
  echo "preflight ${task}: in flight on ${expected_kind} executor"
}

finished_count() {
  timeline "$1" "$2" | jq '[.[] | select(.kind == "agent_run_finished")] | length'
}

assert_in_flight local "$LOCAL_PROJECT" "$LOCAL_TASK_ID"
assert_in_flight remote "$REMOTE_PROJECT" "$REMOTE_TASK_ID"
local_finished_before="$(finished_count "$LOCAL_PROJECT" "$LOCAL_TASK_ID")"
remote_finished_before="$(finished_count "$REMOTE_PROJECT" "$REMOTE_TASK_ID")"

trigger="$(curl --fail --silent --show-error "${update_headers[@]}" \
  -X POST "${update}/update/trigger" \
  --data '{"reason":"restart-continuity-release-drill","force":true}')"
run_id="$(jq -er '.runId' <<<"$trigger")"
echo "update triggered: ${run_id}"

bridge_seen=false
while true; do
  now_epoch="$(date +%s)"
  if (( now_epoch - started_epoch > TIMEOUT_SECONDS )); then
    echo "restart-continuity drill timed out after ${TIMEOUT_SECONDS}s" >&2
    exit 1
  fi

  local_timeline="$(timeline "$LOCAL_PROJECT" "$LOCAL_TASK_ID" 2>/dev/null || echo '[]')"
  remote_timeline="$(timeline "$REMOTE_PROJECT" "$REMOTE_TASK_ID" 2>/dev/null || echo '[]')"
  if jq -e 'any(.[]; .kind == "run_lost_across_restart")' <<<"$local_timeline" >/dev/null \
      || jq -e 'any(.[]; .kind == "run_lost_across_restart")' <<<"$remote_timeline" >/dev/null; then
    echo "a run was lost across the restart" >&2
    exit 1
  fi
  if jq -e 'any(.[]; .kind == "run_continued_after_restart")' <<<"$local_timeline" >/dev/null; then
    bridge_seen=true
  fi

  local_finished="$(jq '[.[] | select(.kind == "agent_run_finished")] | length' <<<"$local_timeline")"
  remote_finished="$(jq '[.[] | select(.kind == "agent_run_finished")] | length' <<<"$remote_timeline")"
  update_result="$(curl --silent --show-error "${update}/update/history?max=20" \
    | jq -r --arg run "$run_id" '.[] | select(.runId == $run) | .status' | head -1)"

  if [[ "$update_result" == "failed" ]]; then
    echo "update-service reported failure for ${run_id}" >&2
    exit 1
  fi
  if [[ "$update_result" == "ok" \
      && "$bridge_seen" == "true" \
      && "$local_finished" -gt "$local_finished_before" \
      && "$remote_finished" -gt "$remote_finished_before" ]]; then
    echo "PASS ${run_id}: update completed; local and remote runs finished; local restart bridge visible"
    exit 0
  fi

  sleep "$POLL_SECONDS"
done
