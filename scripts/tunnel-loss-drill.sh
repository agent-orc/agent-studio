#!/usr/bin/env bash
# AGT-2937 tunnel-loss and fenced-recovery drill (Dossier AGT-W65 D9).
# Runs only isolated in-process fixtures: one drill route per test client,
# temp databases, temp git origins and synthetic clocks. It never touches a
# production tunnel, Task Server, runner service or workspace.
# Runbook: docs/operations/testing/tunnel-loss-drill.md
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
report_dir="${TUNNEL_DRILL_REPORT_DIR:-${repo_root}/docs/operations/testing/tunnel-loss-drill-evidence}"
if [[ "${report_dir}" != /* ]]; then
  report_dir="${repo_root}/${report_dir}"
fi
step_timeout="${TUNNEL_DRILL_STEP_TIMEOUT_SECONDS:-900}"

if [[ "${report_dir}" == / || "${report_dir}" == "${repo_root}" ]]; then
  echo "tunnel drill: report directory must be a dedicated folder" >&2
  exit 2
fi
mkdir -p "${report_dir}"
rm -f "${report_dir}/tunnel-drill-report.md" "${report_dir}/tunnel-drill.jsonl" \
  "${report_dir}/steps.txt" "${report_dir}/runner.log" \
  "${report_dir}/task-server.log" "${report_dir}/integration-resume.log"
export TUNNEL_DRILL_REPORT_DIR="${report_dir}"
status_file="${report_dir}/steps.txt"
: >"${status_file}"
failed=0

run_step() {
  local name="$1" project="$2" filter="$3"
  local log="${report_dir}/${name}.log"
  echo "== ${name}: dotnet test ${project} --filter \"${filter}\""
  if timeout "${step_timeout}" dotnet test "${repo_root}/${project}" --filter "${filter}" >"${log}" 2>&1; then
    local summary
    summary="$(grep -m1 -E 'Passed!|Failed!' "${log}" || true)"
    echo "${name} pass ${summary}" >>"${status_file}"
    printf '%s\n' "${summary}"
    rm -f "${log}"
  else
    local code=$?
    echo "${name} FAIL exit=${code} log=${name}.log" >>"${status_file}"
    failed=1
    grep -E "Passed!|Failed!|\[FAIL\]" "${log}" || true
  fi
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

if [[ -f "${report_dir}/tunnel-drill-report.md" ]]; then
  {
    echo
    echo "## Step receipts"
    echo
    echo 'The `integration-resume` step includes the settled-review restart proof'
    echo '`Restart_before_integration_starts_integrates_afterwards_without_a_new_review`.'
    echo 'It resumes integration from the persisted settlement without another review attempt.'
    echo 'This step uses isolated backend fixtures; its receipt is a test result, not a historical outage time.'
    echo
    echo '```text'
    cat "${status_file}"
    echo '```'
  } >>"${report_dir}/tunnel-drill-report.md"
fi

echo "== steps"
cat "${status_file}"
echo "== report: ${report_dir}/tunnel-drill-report.md"
exit "${failed}"
