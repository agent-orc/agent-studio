#!/usr/bin/env bash
set -euo pipefail

# Real deployment drill for a test-subject Studio. The two supplied tasks must
# already be in Progress, one local and one remote. Never point this at an
# operator's production Studio without first selecting disposable task cards.

: "${STUDIO_URL:=http://127.0.0.1:5000}"
: "${UPDATE_URL:=http://127.0.0.1:5080}"
: "${DRILL_TIMEOUT_SECONDS:=900}"
: "${JOB_RESULTS_DIR:=$(pwd)/restart-drill-results}"
: "${PROJECT:?Set PROJECT to the project registry name}"
: "${LOCAL_TASK_ID:?Set LOCAL_TASK_ID to an in-flight local test task}"
: "${REMOTE_TASK_ID:?Set REMOTE_TASK_ID to an in-flight remote test task}"

command -v curl >/dev/null
command -v jq >/dev/null
mkdir -p "$JOB_RESULTS_DIR"
drill_log="$JOB_RESULTS_DIR/restart-continuity-drill-$(date -u +%Y%m%dT%H%M%SZ).log"
exec > >(tee -a "$drill_log") 2>&1

task_url() {
  local task_id="$1"
  printf '%s/api/tasks/%s?project=%s' "$STUDIO_URL" \
    "$(jq -rn --arg value "$task_id" '$value|@uri')" \
    "$(jq -rn --arg value "$PROJECT" '$value|@uri')"
}

read_task() {
  curl --fail --silent --show-error "$(task_url "$1")"
}

read_timeline() {
  local task_id="$1"
  curl --fail --silent --show-error \
    "$STUDIO_URL/api/tasks/$(jq -rn --arg value "$task_id" '$value|@uri')/timeline?project=$(jq -rn --arg value "$PROJECT" '$value|@uri')"
}

assert_in_flight() {
  local task_id="$1"
  local expected_kind="$2"
  local task
  task="$(read_task "$task_id")"
  jq -e --arg kind "$expected_kind" '
    .state == "3-progress"
    and .executionLocation.executionKind == $kind
    and (.executionLocation.state | endswith("running"))
  ' <<<"$task" >/dev/null
  printf 'preflight task=%s kind=%s state=3-progress\n' "$task_id" "$expected_kind"
}

wait_for_update() {
  local run_id="$1"
  local deadline=$((SECONDS + DRILL_TIMEOUT_SECONDS))
  while (( SECONDS < deadline )); do
    local status phase status_run
    status="$(curl --fail --silent --show-error "$UPDATE_URL/update/status")"
    phase="$(jq -r '.phase // "unknown"' <<<"$status")"
    status_run="$(jq -r '.runId // ""' <<<"$status")"
    printf 'update run=%s phase=%s\n' "$status_run" "$phase"
    if [[ "$status_run" == "$run_id" && "$phase" == "done" ]]; then return 0; fi
    if [[ "$status_run" == "$run_id" && "$phase" =~ ^(failed|rolled-back)$ ]]; then
      return 1
    fi
    sleep 2
  done
  return 1
}

wait_for_terminal_task() {
  local task_id="$1"
  local deadline=$((SECONDS + DRILL_TIMEOUT_SECONDS))
  while (( SECONDS < deadline )); do
    local task state
    task="$(read_task "$task_id")"
    state="$(jq -r '.state' <<<"$task")"
    printf 'task=%s state=%s activity=%s\n' "$task_id" "$state" \
      "$(jq -r '.runActivity.kind // "none"' <<<"$task")"
    if [[ "$state" != "3-progress" ]]; then return 0; fi
    sleep 2
  done
  return 1
}

printf 'restart continuity drill started at %s\n' "$(date -u +%FT%TZ)"
curl --fail --silent --show-error "$STUDIO_URL/healthz" >/dev/null
curl --fail --silent --show-error "$UPDATE_URL/update/health" >/dev/null
assert_in_flight "$LOCAL_TASK_ID" local
assert_in_flight "$REMOTE_TASK_ID" remote
local_timeline_before="$(read_timeline "$LOCAL_TASK_ID")"
remote_timeline_before="$(read_timeline "$REMOTE_TASK_ID")"
local_started_before="$(jq '[.[] | select(.kind == "agent_run_started")] | length' <<<"$local_timeline_before")"
remote_started_before="$(jq '[.[] | select(.kind == "agent_run_started")] | length' <<<"$remote_timeline_before")"
local_run_id="$(jq -r '[.[] | select(.kind == "agent_run_started")][-1].runId // ""' <<<"$local_timeline_before")"

trigger_headers=(-H 'Content-Type: application/json')
if [[ -n "${UPDATE_TOKEN:-}" ]]; then trigger_headers+=(-H "X-Update-Token: $UPDATE_TOKEN"); fi
trigger="$(curl --fail --silent --show-error -X POST "${trigger_headers[@]}" \
  --data '{"reason":"manual","force":true}' "$UPDATE_URL/update/trigger")"
run_id="$(jq -er '.runId' <<<"$trigger")"
printf 'triggered update run=%s\n' "$run_id"
wait_for_update "$run_id"
wait_for_terminal_task "$LOCAL_TASK_ID"
wait_for_terminal_task "$REMOTE_TASK_ID"

local_timeline_after="$(read_timeline "$LOCAL_TASK_ID")"
remote_timeline_after="$(read_timeline "$REMOTE_TASK_ID")"
jq -e --argjson expected "$local_started_before" \
  '[.[] | select(.kind == "agent_run_started")] | length == $expected' \
  <<<"$local_timeline_after" >/dev/null
jq -e --argjson expected "$remote_started_before" \
  '[.[] | select(.kind == "agent_run_started")] | length == $expected' \
  <<<"$remote_timeline_after" >/dev/null
jq -e --arg run_id "$local_run_id" \
  '[.[] | select(.kind == "run_restart_bridged" and (.runId == $run_id or $run_id == ""))] | length == 1' \
  <<<"$local_timeline_after" >/dev/null
printf 'PASS update=%s local=%s remote=%s bridged-events=1 new-attempts=0 completed=%s\n' \
  "$run_id" "$LOCAL_TASK_ID" "$REMOTE_TASK_ID" "$(date -u +%FT%TZ)"
printf 'evidence=%s\n' "$drill_log"
