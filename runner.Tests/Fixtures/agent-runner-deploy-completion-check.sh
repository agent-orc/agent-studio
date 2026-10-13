#!/usr/bin/env bash
# AGT-2985: after a promotion at least one completion must be accepted within
# the watch window, otherwise the helper prints the rollback command. Stable
# 0.9.3 restarted cleanly and then had every completion rejected. AGT-3095:
# the accepted task's artifact uploads must not have been refused with 409.
set -euo pipefail

helper_path="${1:?expected the agent-runner-deploy path}"
fixture_root="$(mktemp -d)"
journal_file="$fixture_root/journal"
outgoing_journal_file="$fixture_root/outgoing-journal"
poll_file="$fixture_root/polls"
scenario=""
new_invocation_id="0123456789abcdef0123456789abcdef"
recorded_activation_epoch=0

cleanup() {
  rm -rf -- "$fixture_root"
}
trap cleanup EXIT

# shellcheck source=/dev/null
source "$helper_path"

last_promotion_value() {
  case "$1" in
    release) printf 'rel-new\n' ;;
    previous) printf 'rel-previous\n' ;;
    activated) printf '%s\n' "$recorded_activation_epoch" ;;
    invocation) printf '%s\n' "$new_invocation_id" ;;
  esac
}

journalctl() {
  local polls=0
  local until_epoch=9999999999
  local argument
  [[ ! -f "$poll_file" ]] || polls="$(<"$poll_file")"
  polls=$((polls + 1))
  printf '%s\n' "$polls" >"$poll_file"
  for argument in "$@"; do
    case "$argument" in
      --until=@*) until_epoch="${argument#--until=@}" ;;
    esac
  done
  if [[ "$scenario" == "late-acceptance" && "$polls" -ge 3 ]]; then
    printf "%s|[10:02:00] [agent-host] task 'AGT-2' handed back to the local board: Done\n" \
      "$recorded_activation_epoch" >>"$journal_file"
  fi
  if [[ "$*" == *"_SYSTEMD_UNIT=agent-runner.service"* \
      && "$*" == *"_SYSTEMD_INVOCATION_ID=$new_invocation_id"* ]]; then
    awk -F '|' -v until="$until_epoch" \
      '$1 <= until { print substr($0, index($0, "|") + 1) }' "$journal_file"
  else
    cat -- "$outgoing_journal_file"
  fi
}

# Echo the logged message so the structured result can be asserted.
logger() {
  printf 'logger: %s\n' "${*: -1}"
}

sleep() {
  SECONDS=$((SECONDS + ${1%.*}))
}

run_scenario() {
  scenario="$1"
  : >"$journal_file"
  : >"$outgoing_journal_file"
  rm -f -- "$poll_file"
  recorded_activation_epoch="$(date +%s)"
  case "$scenario" in
    accepted)
      printf "%s|[10:00:00] [agent-host] remote-runner-completion recorded: outcome Done, state 4-auto-review, result-envelope attached\n" "$recorded_activation_epoch" >>"$journal_file"
      printf "%s|[10:00:00] [agent-host] task 'AGT-1' handed back to the local board: Done\n" "$recorded_activation_epoch" >>"$journal_file"
      ;;
    artifacts-complete)
      printf "%s|[10:00:00] [agent-host] artifact-transfer task=AGT-1 artifacts=complete uploaded=2 notTransferred=0\n" "$recorded_activation_epoch" >>"$journal_file"
      printf "%s|[10:00:01] [agent-host] task 'AGT-1' handed back to the local board: Done\n" "$recorded_activation_epoch" >>"$journal_file"
      ;;
    artifacts-rejected)
      # AGT-3095: the 0.9.4 order settled first, so every upload hit the strict fence.
      for artifact in deliverables.md status.md agt-2906-api-readback.json preparation-manifest.json; do
        printf '%s|[10:00:01] [agent-host] artifact-transfer outcome=ArtifactTransferFailed task=AGT-1 result artifact %s 0.01 MB upload failed after 1 attempt(s) (POST /api/runner/artifacts -> 409: {"status":"invalidState","message":"RunAttempt is Completed and no longer has write authority."}); not transferred\n' \
          "$recorded_activation_epoch" "$artifact" >>"$journal_file"
      done
      printf "%s|[10:00:01] [agent-host] artifact-transfer task=AGT-1 artifacts=partial uploaded=0 notTransferred=4\n" "$recorded_activation_epoch" >>"$journal_file"
      printf "%s|[10:00:00] [agent-host] task 'AGT-1' handed back to the local board: Done\n" "$recorded_activation_epoch" >>"$journal_file"
      ;;
    heartbeat-lease-lost)
      printf "%s|[10:00:00] [agent-host] lease lost; terminating CLI process group: InvalidState - RunAttempt is Completed and no longer has write authority.\n" "$recorded_activation_epoch" >>"$journal_file"
      printf "%s|[10:00:00] [agent-host] artifact-transfer task=AGT-1 artifacts=complete uploaded=2 notTransferred=0\n" "$recorded_activation_epoch" >>"$journal_file"
      printf "%s|[10:00:01] [agent-host] task 'AGT-1' handed back to the local board: Done\n" "$recorded_activation_epoch" >>"$journal_file"
      ;;
    artifacts-too-large)
      printf "%s|[10:00:00] [agent-host] artifact-transfer outcome=ArtifactTooLarge task=AGT-1 result artifact trace.zip 9.20 MB exceeds the 8 MB per-file limit; not transferred\n" "$recorded_activation_epoch" >>"$journal_file"
      printf "%s|[10:00:00] [agent-host] artifact-transfer outcome=ArtifactTooLarge task=AGT-1 result artifact video.webm 12.00 MB was refused because artifact storage is full (HTTP 507); not transferred\n" "$recorded_activation_epoch" >>"$journal_file"
      printf "%s|[10:00:00] [agent-host] artifact-transfer task=AGT-1 artifacts=partial uploaded=0 notTransferred=2\n" "$recorded_activation_epoch" >>"$journal_file"
      printf "%s|[10:00:01] [agent-host] task 'AGT-1' handed back to the local board: Done\n" "$recorded_activation_epoch" >>"$journal_file"
      ;;
    other-task-rejected)
      printf '%s|[10:00:00] [agent-host] artifact-transfer outcome=ArtifactTransferFailed task=AGT-10 result artifact status.md 0.01 MB upload failed after 1 attempt(s) (POST /api/runner/artifacts -> 409: {"status":"invalidState"}); not transferred\n' "$recorded_activation_epoch" >>"$journal_file"
      printf "%s|[10:00:00] [agent-host] artifact-transfer task=AGT-1 artifacts=complete uploaded=1 notTransferred=0\n" "$recorded_activation_epoch" >>"$journal_file"
      printf "%s|[10:00:01] [agent-host] task 'AGT-1' handed back to the local board: Done\n" "$recorded_activation_epoch" >>"$journal_file"
      ;;
    early-new-invocation)
      printf "%s|[09:59:55] [agent-host] task 'AGT-early' handed back to the local board: Done\n" \
        "$((recorded_activation_epoch - 5))" >>"$journal_file"
      ;;
    outgoing-acceptance)
      printf "[10:00:00] [agent-host] task 'AGT-old' handed back to the local board: Done\n" >>"$outgoing_journal_file"
      printf '%s|[10:00:00] [agent-host] slot failed: AgentRunner.TaskServerException: POST /api/runner/completion -> 400\n' "$recorded_activation_epoch" >>"$journal_file"
      ;;
    rejected)
      printf '%s|[10:00:00] [agent-host] slot failed: AgentRunner.TaskServerException: POST /api/runner/completion -> 400: {"message":"Session continuation evidence does not match the fenced attempt."}\n' "$recorded_activation_epoch" >>"$journal_file"
      printf '%s|[10:05:00] [agent-host] slot failed: AgentRunner.TaskServerException: POST /api/runner/completion -> 400: {"message":"Session continuation evidence does not match the fenced attempt."}\n' "$recorded_activation_epoch" >>"$journal_file"
      ;;
    expired-late-acceptance)
      recorded_activation_epoch=$((recorded_activation_epoch - completion_watch_seconds - 1))
      printf "%s|[10:10:01] [agent-host] task 'AGT-late' handed back to the local board: Done\n" \
        "$((recorded_activation_epoch + completion_watch_seconds + 1))" >>"$journal_file"
      ;;
    expired-on-time-acceptance)
      recorded_activation_epoch=$((recorded_activation_epoch - completion_watch_seconds - 1))
      printf "%s|[10:10:00] [agent-host] task 'AGT-on-time' handed back to the local board: Done\n" \
        "$((recorded_activation_epoch + completion_watch_seconds))" >>"$journal_file"
      ;;
  esac
  local status=0
  local output
  output="$( (verify_completions) 2>&1)" || status=$?
  printf '%s-status=%s\n' "$scenario" "$status"
  printf '%s-polls=%s\n' "$scenario" "$(<"$poll_file")"
  printf '%s\n' "$output" | sed "s/^/$scenario-output: /"
}

run_scenario accepted
run_scenario early-new-invocation
run_scenario late-acceptance
run_scenario outgoing-acceptance
run_scenario rejected
run_scenario idle
run_scenario expired-late-acceptance
run_scenario expired-on-time-acceptance
run_scenario artifacts-complete
run_scenario artifacts-rejected
run_scenario heartbeat-lease-lost
run_scenario artifacts-too-large
run_scenario other-task-rejected
