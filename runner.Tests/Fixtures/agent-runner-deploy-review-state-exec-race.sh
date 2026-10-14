#!/usr/bin/env bash
set -euo pipefail

helper_path="${1:?expected the agent-runner-deploy path}"
fixture_root="$(mktemp -d)"
proc_dir="$fixture_root/proc"
patched_helper="$fixture_root/agent-runner-deploy"
live_state_dir="$fixture_root/live-state"
fixture_pid=123
poll_count=0
scenario="exec"

cleanup() {
  rm -rf -- "$fixture_root"
}
trap cleanup EXIT

mkdir -p "$proc_dir/$fixture_pid" "$live_state_dir"
sed "s#^readonly proc_root=.*#readonly proc_root=\"$proc_dir\"#" \
  "$helper_path" >"$patched_helper"
# shellcheck source=/dev/null
source "$patched_helper"

systemctl() {
  case "$1" in
    is-active) return 0 ;;
    show) printf '%s\n' "$fixture_pid" ;;
    *) return 64 ;;
  esac
}

sleep() {
  poll_count=$((poll_count + 1))
  if [[ "$scenario" == "exec" ]]; then
    printf 'RUNNER_STATE_DIR=%s\0' "$live_state_dir" >"$proc_dir/$fixture_pid/environ"
  fi
  SECONDS=$((SECONDS + 1))
}

printf 'PARENT_SHELL=1\0' >"$proc_dir/$fixture_pid/environ"
[[ "$(review_state_dir)" == "$live_state_dir" ]]

scenario="never-exec"
SECONDS=0
printf 'PARENT_SHELL=1\0' >"$proc_dir/$fixture_pid/environ"
if review_state_dir >"$fixture_root/unexpected-state" 2>"$fixture_root/error"; then
  printf 'review_state_dir accepted a missing process environment value\n' >&2
  exit 1
fi
[[ ! -s "$fixture_root/unexpected-state" ]]
grep -Fq "invalid effective RUNNER_STATE_DIR: ''" "$fixture_root/error"
[[ "$poll_count" -ge 1 && "$poll_count" -le 5 ]]

printf 'pre-exec-value-retried=true\n'
printf 'missing-value-bounded=true\n'
