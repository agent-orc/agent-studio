#!/usr/bin/env bash
# AGT-2937 tunnel-loss and fenced-recovery drill (Dossier AGT-W65 D9).
# Runs only isolated in-process fixtures: one drill route per test client,
# temp databases, temp git origins and synthetic clocks. It never touches a
# production tunnel, Task Server, runner service or workspace.
# Runbook: docs/operations/testing/tunnel-loss-drill.md
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
report_dir="${TUNNEL_DRILL_REPORT_DIR:-${JOB_RESULTS_DIR:-${repo_root}/tunnel-drill-results}/tunnel-drill}"
step_timeout="${TUNNEL_DRILL_STEP_TIMEOUT_SECONDS:-900}"

rm -rf "${report_dir}"
mkdir -p "${report_dir}"
export TUNNEL_DRILL_REPORT_DIR="${report_dir}"
status_file="${report_dir}/steps.txt"
: >"${status_file}"
failed=0

run_step() {
  local name="$1" project="$2" filter="$3"
  local log="${report_dir}/${name}.log"
  echo "== ${name}: dotnet test ${project} --filter \"${filter}\""
  if timeout "${step_timeout}" dotnet test "${repo_root}/${project}" --filter "${filter}" >"${log}" 2>&1; then
    echo "${name} pass" >>"${status_file}"
  else
    local code=$?
    echo "${name} FAIL exit=${code} log=${name}.log" >>"${status_file}"
    failed=1
  fi
  grep -E "Passed!|Failed!|\[FAIL\]" "${log}" || true
}

# 1. Runner half: short interruption, beyond-authority stop, exact re-adoption
#    after daemon restart, superseded rejection, git quarantine, lost ack.
run_step runner runner.Tests "FullyQualifiedName~AgentRunner.Tests.TunnelLossDrillTests"
# 2. Task Server half: server restart, exact coding/review re-adoption,
#    superseded coding/review generations, replacement claim with a new fence.
run_step task-server task-server.Tests "FullyQualifiedName~Tunnel_drill"
# 3. Already-settled review delivery resumes integration without a new review
#    (AutoReviewDeliveryResumeService restart drill, AGT-2860/AGT-2936).
run_step integration-resume backend.Tests "FullyQualifiedName~AutoReviewRestartDrillTests"

echo "== steps"
cat "${status_file}"
echo "== report: ${report_dir}/tunnel-drill-report.md"
exit "${failed}"
