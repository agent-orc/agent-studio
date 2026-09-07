#!/bin/sh
set -eu

if [ "${1:-}" = "--version" ]; then
  printf 'topology-agent 1.0.0\n'
  exit 0
fi

mkdir -p "$JOB_RESULTS_DIR"
printf 'container topology proof\n' > "$JOB_RESULTS_DIR/compose-proof.txt"
printf '{"type":"agent_message","text":"container claim complete"}\n'
printf '{"type":"tool","name":"compose-fixture"}\n'
printf '[[TASK_DONE]]\n'
