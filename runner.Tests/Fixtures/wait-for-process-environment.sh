#!/usr/bin/env bash

wait_for_process_environment_value() {
  local pid="$1"
  local key="$2"
  local expected_value="$3"
  local attempt

  for ((attempt = 0; attempt < 50; attempt++)); do
    if [[ -r "/proc/$pid/environ" ]] \
        && tr '\0' '\n' <"/proc/$pid/environ" \
          | grep -Fx -- "$key=$expected_value" >/dev/null; then
      return 0
    fi
    command sleep 0.1
  done
  printf 'timed out waiting for %s in process %s environment\n' "$key" "$pid" >&2
  return 1
}
