#!/usr/bin/env bash
set -euo pipefail

helper_path="${1:?expected the agent-runner-deploy path}"
fixture_root="$(mktemp -d)"
patched_helper="$fixture_root/agent-runner-deploy"
stale_environment="$fixture_root/runner-review.env"
effective_environment="$fixture_root/review.env"
live_state_dir="$fixture_root/live-state"
inactive_state_dir="$fixture_root/inactive-state"
candidate_binary="$fixture_root/candidate-agent-host"
legacy_binary="$fixture_root/legacy-agent-host"
legacy_log="$fixture_root/legacy-invoked"
daemon_pid=""
scenario="active"

cleanup() {
  if [[ "$daemon_pid" =~ ^[1-9][0-9]*$ ]]; then
    kill "$daemon_pid" >/dev/null 2>&1 || true
    wait "$daemon_pid" >/dev/null 2>&1 || true
  fi
  rm -rf -- "$fixture_root"
}
trap cleanup EXIT

sed \
  -e "s#^readonly -a review_env_files=.*#readonly -a review_env_files=(\"$stale_environment\" \"$effective_environment\")#" \
  -e "s#^readonly agent_host_binary=.*#readonly agent_host_binary=\"$legacy_binary\"#" \
  "$helper_path" >"$patched_helper"

printf 'RUNNER_STATE_DIR=%s\n' "$fixture_root/stale-state" >"$stale_environment"
printf "RUNNER_STATE_DIR='%s'\n" "$inactive_state_dir" >"$effective_environment"
printf '%s\n' \
  '#!/usr/bin/env bash' \
  'printf '\''candidate-guard-args=%s\n'\'' "$*"' \
  >"$candidate_binary"
printf '#!/usr/bin/env bash\nprintf invoked >%q\nexit 99\n' "$legacy_log" >"$legacy_binary"
chmod 0755 "$candidate_binary" "$legacy_binary"
mkdir -p "$live_state_dir" "$inactive_state_dir"

# shellcheck source=/dev/null
source "$patched_helper"

RUNNER_STATE_DIR="$live_state_dir" /usr/bin/sleep 30 &
daemon_pid="$!"

systemctl() {
  local action="${1:-}"
  shift || true
  case "$action" in
    is-active)
      [[ "$scenario" == "active" ]]
      ;;
    show)
      if [[ "$*" == "--property=MainPID --value agent-runner-review.service" ]]; then
        printf '%s\n' "$daemon_pid"
      elif [[ "$*" == "--property=EnvironmentFiles --value agent-runner-review.service" ]]; then
        printf '%s (ignore_errors=no)\n' "$effective_environment"
      else
        return 64
      fi
      ;;
    *) return 64 ;;
  esac
}

logger() {
  :
}

resolved_live_state="$(review_state_dir)"
[[ "$resolved_live_state" == "$live_state_dir" ]]
assert_review_restart_is_safe "$candidate_binary"
[[ "$review_guard_state_dir" == "$live_state_dir" ]]
[[ ! -e "$legacy_log" ]]

scenario="inactive"
resolved_inactive_state="$(review_state_dir)"
[[ "$resolved_inactive_state" == "$inactive_state_dir" ]]

printf 'live-state-dir=%s\n' "$resolved_live_state"
printf 'inactive-loaded-state-dir=%s\n' "$resolved_inactive_state"
printf 'candidate-used=true\n'
printf 'legacy-current-used=false\n'
