#!/usr/bin/env bash
# AGT-2985: after a promotion at least one completion must be accepted within
# the watch window, otherwise the helper prints the rollback command. Stable
# 0.9.3 restarted cleanly and then had every completion rejected.
set -euo pipefail

helper_path="${1:?expected the agent-runner-deploy path}"
fixture_root="$(mktemp -d)"
journal_file="$fixture_root/journal"
poll_file="$fixture_root/polls"
scenario=""

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
    activated) date +%s ;;
  esac
}

journalctl() {
  [[ "$*" == "-u agent-runner.service --since @"*" -o cat --no-pager" ]] || return 64
  local polls=0
  [[ ! -f "$poll_file" ]] || polls="$(<"$poll_file")"
  polls=$((polls + 1))
  printf '%s\n' "$polls" >"$poll_file"
  if [[ "$scenario" == "late-acceptance" && "$polls" -ge 3 ]]; then
    printf "[10:02:00] [agent-host] task 'AGT-2' handed back to the local board: Done\n" >>"$journal_file"
  fi
  cat -- "$journal_file"
}

logger() { :; }

sleep() {
  SECONDS=$((SECONDS + ${1%.*}))
}

run_scenario() {
  scenario="$1"
  : >"$journal_file"
  rm -f -- "$poll_file"
  case "$scenario" in
    accepted)
      printf "[10:00:00] [agent-host] remote-runner-completion recorded: outcome Done, state 4-auto-review, result-envelope attached\n" >>"$journal_file"
      printf "[10:00:00] [agent-host] task 'AGT-1' handed back to the local board: Done\n" >>"$journal_file"
      ;;
    rejected)
      printf '[10:00:00] [agent-host] slot failed: AgentRunner.TaskServerException: POST /api/runner/completion -> 400: {"message":"Session continuation evidence does not match the fenced attempt."}\n' >>"$journal_file"
      printf '[10:05:00] [agent-host] slot failed: AgentRunner.TaskServerException: POST /api/runner/completion -> 400: {"message":"Session continuation evidence does not match the fenced attempt."}\n' >>"$journal_file"
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
run_scenario late-acceptance
run_scenario rejected
run_scenario idle
