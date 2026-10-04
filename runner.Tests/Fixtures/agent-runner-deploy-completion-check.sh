#!/usr/bin/env bash
# AGT-2985: after a promotion at least one completion must be accepted within
# the watch window, otherwise the helper prints the rollback command. Stable
# 0.9.3 restarted cleanly and then had every completion rejected.
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

logger() { :; }

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
