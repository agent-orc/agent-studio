#!/usr/bin/env bash
# Regression for two isolated stacks using different ports in one checkout.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
fixture="$(mktemp -d)"
trap 'rm -rf -- "${fixture}"' EXIT
cp "${repo_root}/api.sh" "${fixture}/api.sh"

for port in 5101 5102; do
  printf 'pid-%s\n' "${port}" > "${fixture}/.api.${port}.pid"
  printf 'launcher-%s\n' "${port}" > "${fixture}/.api.${port}.launcher.pid"
  printf 'out-%s\n' "${port}" > "${fixture}/.api.${port}.log.out"
  printf 'err-%s\n' "${port}" > "${fixture}/.api.${port}.log.err"
done

(
  PORT=5101
  API_PORT_OVERRIDE=1
  ATP_ALLOW_DEV_BACKEND=1
  source "${fixture}/api.sh"
  require_port_inspection() { return 0; }
  listener_pids() { return 0; }
  sweepable_project_pids() { return 0; }
  stop_targets() { return 0; }
  cmd_stop >/dev/null
)
[[ ! -e "${fixture}/.api.5101.pid" && ! -e "${fixture}/.api.5101.launcher.pid" ]]
[[ "$(cat "${fixture}/.api.5102.pid")" == pid-5102 ]]
[[ "$(cat "${fixture}/.api.5102.launcher.pid")" == launcher-5102 ]]

(
  PORT=5101
  API_PORT_OVERRIDE=1
  ATP_ALLOW_DEV_BACKEND=1
  source "${fixture}/api.sh"
  require_dev_checkout() { return 0; }
  prepare_launch_command() {
    API_LAUNCH_DIRECTORY="${fixture}"
    API_LAUNCH_COMMAND=(bash -c 'exit 1')
  }
  require_port_inspection() { return 0; }
  listener_pids() { return 0; }
  identity_set() { return 0; }
  launch_activity_pids() { return 0; }
  START_TIMEOUT_SECS=1
  cmd_start >/dev/null 2>&1
) || true
[[ "$(cat "${fixture}/.api.5101.log.out."*.bak)" == out-5101 ]]
[[ "$(cat "${fixture}/.api.5101.log.err."*.bak)" == err-5101 ]]
[[ "$(cat "${fixture}/.api.5102.log.out")" == out-5102 ]]
[[ "$(cat "${fixture}/.api.5102.log.err")" == err-5102 ]]
[[ -z "$(find "${fixture}" -name '.api.5102.*.bak' -print -quit)" ]]
printf 'Port-specific API control files passed.\n'
