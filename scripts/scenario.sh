#!/usr/bin/env bash
# Deployment regression scenario (AGT-2739): one seeded fixture driven through
# an ordered set of steps against the Task Server / Runner topology, in three
# targets from one definition. See
# docs/operations/testing/deployment-scenario.md for what it proves and how to
# read the report.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

usage() {
    cat <<'EOF'
Usage: scripts/scenario.sh --target inproc|compose|remote --level smoke|full [options]

Targets:
  inproc   Boots Task Server + Runner as sibling processes (dotnet test). No
           Docker required; runs on Windows and Linux in under three minutes
           at --level smoke.
  compose  Runs against the docker-compose stack. --level smoke reuses the
           existing default-profile compose smoke checks (orchestrator-api +
           frontend, scripts/compose-smoke-test.sh). --level full (distributed
           + runner profiles driving a real coding/review lifecycle) needs a
           deterministic fake CLI image on those profiles, which does not
           exist yet; see the "known gaps" section of
           docs/operations/testing/deployment-scenario.md.
  remote   Runs only the non-destructive management-plane steps (bootstrap a
           scenario principal, create a scenario project/task, take a backup,
           archive the task) against an already-deployed Task Server.
           Requires --remote-url and --remote-token. Does not drive a
           coding/review run: that needs a runner already attached to that
           deployment, which this script does not provision.

Levels:
  smoke    The first six steps in testsupport/scenario/steps.json (bootstrap
           principals through auto-review).
  full     Every step in testsupport/scenario/steps.json.

Options:
  --report-dir DIR      Where to write the JUnit + Markdown report
                         (default: artifacts/scenario-reports).
  --configuration NAME  .NET build configuration for --target inproc
                         (default: Debug).
  --remote-url URL      Base URL for --target remote.
  --remote-token TOKEN  Bearer token for --target remote.
  -h, --help            Show this help.
EOF
}

target=""
level=""
report_dir="$repo_root/artifacts/scenario-reports"
configuration="Debug"
remote_url=""
remote_token=""

while [ $# -gt 0 ]; do
    case "$1" in
        --target) target="$2"; shift 2 ;;
        --level) level="$2"; shift 2 ;;
        --report-dir) report_dir="$2"; shift 2 ;;
        --configuration) configuration="$2"; shift 2 ;;
        --remote-url) remote_url="$2"; shift 2 ;;
        --remote-token) remote_token="$2"; shift 2 ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown argument: $1" >&2; usage; exit 2 ;;
    esac
done

case "$target" in
    inproc|compose|remote) ;;
    *) echo "error: --target must be inproc, compose, or remote" >&2; exit 2 ;;
esac
case "$level" in
    smoke|full) ;;
    *) echo "error: --level must be smoke or full" >&2; exit 2 ;;
esac

mkdir -p "$report_dir"

run_inproc() {
    echo "scenario: building task-server.Tests ($configuration)..." >&2
    dotnet build "$repo_root/task-server.Tests/TaskServer.Tests.csproj" --configuration "$configuration" --nologo

    local test_name="Deployment_regression_scenario_${level}"
    echo "scenario: running $test_name..." >&2
    SCENARIO_REPORT_DIR="$report_dir" dotnet test "$repo_root/task-server.Tests/TaskServer.Tests.csproj" \
        --configuration "$configuration" --no-build \
        --filter "FullyQualifiedName~TaskServer.Tests.ScenarioTests.$test_name" \
        --logger "console;verbosity=normal"
}

run_compose() {
    if [ "$level" = "full" ]; then
        cat >&2 <<'EOF'
scenario: --target compose --level full is not implemented yet. Driving the
coding/review lifecycle in Docker needs a deterministic fake coding/review CLI
image on the `distributed` + `runner` compose profiles, which does not exist
yet (docs/operations/testing/deployment-scenario.md, "known gaps"). Use
--target inproc --level full for the equivalent proof today.
EOF
        exit 3
    fi

    echo "scenario: running compose smoke checks (default profile: orchestrator-api + frontend)..." >&2
    local junit="$report_dir/scenario-compose-smoke.junit.xml"
    local markdown="$report_dir/scenario-compose-smoke.md"
    local status=0
    "$repo_root/scripts/compose-smoke-test.sh" || status=$?

    if [ "$status" -eq 0 ]; then
        cat >"$junit" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<testsuite name="deployment-regression-scenario.compose.smoke" tests="1" failures="0" skipped="0">
  <testcase classname="deployment-regression-scenario.compose" name="compose-smoke: default profile boots healthy and serves the API and UI"/>
</testsuite>
EOF
        printf '# Deployment regression scenario: compose / smoke\n\n**Result: PASSED**\n\nDelegates to scripts/compose-smoke-test.sh (single hermetic check, not yet broken into per-step evidence).\n' >"$markdown"
    else
        cat >"$junit" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<testsuite name="deployment-regression-scenario.compose.smoke" tests="1" failures="1" skipped="0">
  <testcase classname="deployment-regression-scenario.compose" name="compose-smoke: default profile boots healthy and serves the API and UI">
    <failure message="scripts/compose-smoke-test.sh exited $status"/>
  </testcase>
</testsuite>
EOF
        printf '# Deployment regression scenario: compose / smoke\n\n**Result: FAILED**\n\nscripts/compose-smoke-test.sh exited %s.\n' "$status" >"$markdown"
    fi
    exit "$status"
}

run_remote() {
    if [ -z "$remote_url" ] || [ -z "$remote_token" ]; then
        echo "error: --target remote requires --remote-url and --remote-token" >&2
        exit 2
    fi
    if [ "$level" = "full" ]; then
        echo "scenario: --target remote only runs the non-destructive management-plane steps regardless of --level; see docs/operations/testing/deployment-scenario.md." >&2
    fi
    REPORT_DIR="$report_dir" "$repo_root/scripts/scenario-remote-smoke.sh" "$remote_url" "$remote_token"
}

case "$target" in
    inproc) run_inproc ;;
    compose) run_compose ;;
    remote) run_remote ;;
esac
