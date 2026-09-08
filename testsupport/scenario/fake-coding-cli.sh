#!/bin/sh
set -eu

if [ "${1:-}" = "--version" ]; then
  printf 'scenario-coding-agent 1.0.0\n'
  exit 0
fi

while [ ! -f "$SCENARIO_RELEASE_FILE" ]; do
  sleep 0.05
done

passing_status=0
failing_status=0
sh tests/known-passing.sh || passing_status=$?
sh tests/known-failing.sh || failing_status=$?
{
  printf 'known-passing.sh exit=%s\n' "$passing_status"
  printf 'known-failing.sh exit=%s\n' "$failing_status"
} > scenario-run-log.txt
git add scenario-run-log.txt
git -c user.name="Scenario Agent" -c user.email=scenario-agent@example.invalid \
  commit -m "scenario: record fixture check results" --quiet
git push origin HEAD --quiet
mkdir -p "$JOB_RESULTS_DIR"
cp scenario-run-log.txt "$JOB_RESULTS_DIR/scenario-run-log.txt"
printf '{"type":"agent_message","text":"ran known-passing.sh (exit %s) and known-failing.sh (exit %s)"}\n' \
  "$passing_status" "$failing_status"
printf '{"type":"tool","name":"scenario-fixture-tests"}\n'
printf '[[TASK_DONE]]\n'
