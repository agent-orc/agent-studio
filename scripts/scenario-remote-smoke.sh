#!/usr/bin/env bash
# The --target remote half of scripts/scenario.sh: the non-destructive
# management-plane slice of the deployment regression scenario (AGT-2739),
# run against an already-deployed Task Server. Does not drive a coding/review
# run (that needs a runner already attached to that deployment); see
# docs/operations/testing/deployment-scenario.md.
set -euo pipefail

base_url="${1:?usage: scenario-remote-smoke.sh <base-url> <bearer-token>}"
token="${2:?usage: scenario-remote-smoke.sh <base-url> <bearer-token>}"
report_dir="${REPORT_DIR:-artifacts/scenario-reports}"
mkdir -p "$report_dir"

protocol_header="X-Task-Protocol-Version: 2"
client_header="X-Task-Client-Version: scenario-remote-smoke"
auth_header="Authorization: Bearer $token"
stamp="$(date -u +%Y%m%d%H%M%S)"
suffix="scenario-remote-${stamp}-$$"

junit="$report_dir/scenario-remote-smoke.junit.xml"
markdown="$report_dir/scenario-remote-smoke.md"
steps=()

record() {
    steps+=("$1=$2")
}

api() {
    curl --fail --silent --show-error \
        -H "$protocol_header" -H "$client_header" -H "$auth_header" \
        -H 'Content-Type: application/json' \
        "$@"
}

fail() {
    echo "scenario-remote-smoke: $1" >&2
    write_report 1
    exit 1
}

write_report() {
    local status="$1"
    local failures=0
    [ "$status" -ne 0 ] && failures=1
    {
        printf '<?xml version="1.0" encoding="utf-8"?>\n'
        printf '<testsuite name="deployment-regression-scenario.remote.smoke" tests="%s" failures="%s" skipped="0">\n' "${#steps[@]}" "$failures"
        for step in "${steps[@]}"; do
            printf '  <testcase classname="deployment-regression-scenario.remote" name="%s"/>\n' "${step%%=*}"
        done
        [ "$failures" -eq 1 ] && printf '  <testcase classname="deployment-regression-scenario.remote" name="remote-smoke"><failure message="see stderr"/></testcase>\n'
        printf '</testsuite>\n'
    } >"$junit"
    {
        printf '# Deployment regression scenario: remote / smoke\n\n'
        if [ "$status" -eq 0 ]; then printf '**Result: PASSED**\n\n'; else printf '**Result: FAILED**\n\n'; fi
        printf '| Step | Evidence |\n|---|---|\n'
        for step in "${steps[@]}"; do
            printf '| %s | %s |\n' "${step%%=*}" "${step#*=}"
        done
    } >"$markdown"
}

curl --fail --silent "$base_url/readyz" >/dev/null || fail "readyz did not respond"
record "readyz" "reachable"

principal_id="runner:${suffix}"
principal_response="$(api -X POST "$base_url/api/v1/management/principals" \
    -d "{\"principalId\":\"$principal_id\",\"kind\":\"runner\",\"runnerId\":\"$suffix\"}")" \
    || fail "principal bootstrap failed"
echo "$principal_response" | jq -e '.credential' >/dev/null || fail "principal response had no credential"
record "bootstrap-principals" "issued credential for $principal_id"

workspace_response="$(api -X POST "$base_url/api/v1/workspaces" -d "{\"name\":\"Remote scenario $stamp\"}")" \
    || fail "workspace creation failed"
workspace_id="$(echo "$workspace_response" | jq -r '.workspaceId')"

project_response="$(api -X POST "$base_url/api/v1/projects" \
    -d "{\"workspaceId\":\"$workspace_id\",\"name\":\"Remote Scenario\",\"taskKeyPrefix\":\"RMT\"}")" \
    || fail "project creation failed"
project_id="$(echo "$project_response" | jq -r '.projectId')"

task_response="$(api -X POST "$base_url/api/v1/projects/$project_id/tasks" \
    -d '{"title":"Remote scenario probe","body":"Non-destructive remote scenario smoke check (AGT-2739).","state":"0-backlog"}')" \
    || fail "task creation failed"
task_key="$(echo "$task_response" | jq -r '.taskKey')"
task_version="$(echo "$task_response" | jq -r '.version')"
record "create-task" "task $task_key created in project $project_id"

backup_response="$(api -X POST "$base_url/api/v1/management/backups" -d '{"name":"scenario-remote-smoke"}')" \
    || fail "backup failed"
backup_sha="$(echo "$backup_response" | jq -r '.sha256')"
record "backup" "sha256=${backup_sha:0:12}..."

archive_response="$(api -X PUT "$base_url/api/v1/projects/$project_id/tasks/$task_key" \
    -d "{\"title\":null,\"body\":null,\"state\":\"7-archive\",\"expectedVersion\":$task_version}")" \
    || fail "task archive (cleanup) failed"
echo "$archive_response" | jq -e '.state == "7-archive"' >/dev/null || fail "task did not archive"
record "cleanup" "task $task_key archived"

write_report 0
printf '%s\n' "${steps[@]}"

