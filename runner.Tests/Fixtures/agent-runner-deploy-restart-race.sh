#!/usr/bin/env bash
set -euo pipefail

helper_path="${1:?expected the agent-runner-deploy path}"
# shellcheck source=/dev/null
source "$helper_path"

fixture_root="$(mktemp -d)"
environment_file="$fixture_root/runner-review.env"
new_main_pid_file="$fixture_root/new-main-pid"
show_count_file="$fixture_root/show-count"
old_main_pid=""
detached_worker_pid=""
new_main_pid=""
unit_active=1
old_main_parallelism=""

cleanup() {
  local pid
  [[ ! -f "$new_main_pid_file" ]] || new_main_pid="$(<"$new_main_pid_file")"
  for pid in "$old_main_pid" "$detached_worker_pid" "$new_main_pid"; do
    [[ "$pid" =~ ^[1-9][0-9]*$ ]] || continue
    kill "$pid" >/dev/null 2>&1 || true
    wait "$pid" >/dev/null 2>&1 || true
  done
  rm -rf -- "$fixture_root"
}
trap cleanup EXIT

# The main unit supplies the default value. The role EnvironmentFile is applied
# later by systemd and must win for the replacement daemon.
printf 'RUNNER_MAX_PARALLELISM=6\n' >"$environment_file"
RUNNER_MAX_PARALLELISM=2 sleep 30 &
old_main_pid="$!"
RUNNER_MAX_PARALLELISM=2 sleep 30 &
detached_worker_pid="$!"
printf '0\n' >"$show_count_file"
old_main_parallelism="$(read_process_environment_value "$old_main_pid" RUNNER_MAX_PARALLELISM)"

systemctl() {
  local action="${1:-}"
  shift || true

  case "$action" in
    is-active)
      ((unit_active == 1))
      ;;
    kill)
      [[ "$*" == "--kill-whom=main --signal=SIGTERM agent-runner-review.service" ]]
      kill "$old_main_pid" >/dev/null 2>&1 || true
      wait "$old_main_pid" >/dev/null 2>&1 || true
      unit_active=0
      ;;
    start)
      [[ "$*" == "agent-runner-review.service" ]]
      local environment_file_value
      environment_file_value="$(awk -F= '$1 == "RUNNER_MAX_PARALLELISM" { print $2 }' "$environment_file")"
      RUNNER_MAX_PARALLELISM="$environment_file_value" /usr/bin/sleep 30 >/dev/null 2>&1 &
      printf '%s\n' "$!" >"$new_main_pid_file"
      unit_active=1
      ;;
    show)
      local show_count
      show_count="$(( $(<"$show_count_file") + 1 ))"
      printf '%s\n' "$show_count" >"$show_count_file"
      case "$show_count" in
        1)
          printf 'ActiveState=active\nMainPID=%s\n' "$old_main_pid"
          ;;
        2)
          printf 'ActiveState=activating\nMainPID=0\n'
          ;;
        *)
          printf 'ActiveState=active\nMainPID=%s\n' "$(<"$new_main_pid_file")"
          ;;
      esac
      ;;
    *)
      return 64
      ;;
  esac
}

# Keep the polling deterministic while allowing fake systemd to start the
# replacement daemon with the real sleep binary.
sleep() {
  if [[ "${1:-}" == "30" ]]; then
    command sleep "$@"
  fi
}

selected_pid="$(restart_unit_and_wait_for_new_main_pid \
  agent-runner-review.service "$old_main_pid" 2)"
new_main_pid="$(<"$new_main_pid_file")"

[[ "$selected_pid" == "$new_main_pid" ]]
[[ "$selected_pid" != "$old_main_pid" ]]
[[ "$selected_pid" != "$detached_worker_pid" ]]
[[ "$old_main_parallelism" == "2" ]]
[[ "$(read_process_environment_value "$selected_pid" RUNNER_MAX_PARALLELISM)" == "6" ]]
kill -0 "$detached_worker_pid"

printf 'old-main-pid=%s\n' "$old_main_pid"
printf 'old-main-RUNNER_MAX_PARALLELISM=2\n'
printf 'first-post-restart-state=active old-main-pid\n'
printf 'legacy-single-read-result=process-environment-mismatch expected=6 actual=2\n'
printf 'detached-worker-pid=%s\n' "$detached_worker_pid"
printf 'detached-worker-alive-after-restart=true\n'
printf 'new-main-pid=%s\n' "$new_main_pid"
printf 'main-unit-default-RUNNER_MAX_PARALLELISM=2\n'
printf 'role-EnvironmentFile-RUNNER_MAX_PARALLELISM=6\n'
printf 'effective-RUNNER_MAX_PARALLELISM=6\n'
