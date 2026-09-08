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
           frontend, scripts/compose-smoke-test.sh). --level full boots the
           distributed + runner profiles with the deterministic scenario CLI,
           then drives the same typed full manifest used by inproc.
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
    if [ "$level" = "smoke" ]; then
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
            printf '# Deployment regression scenario: compose / smoke\n\n**Result: PASSED**\n\nDelegates to scripts/compose-smoke-test.sh.\n' >"$markdown"
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
        return "$status"
    fi

    run_compose_full
}

free_port() {
    python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1", 0)); print(s.getsockname()[1]); s.close()'
}

wait_for_http() {
    local url="$1"
    local attempt
    for attempt in $(seq 1 120); do
        if curl --fail --silent --output /dev/null "$url"; then
            return 0
        fi
        sleep 0.5
    done
    echo "scenario: timed out waiting for $url" >&2
    return 1
}

run_compose_full() {
    echo "scenario: building the typed scenario driver ($configuration)..." >&2
    dotnet build "$repo_root/task-server.Tests/TaskServer.Tests.csproj" --configuration "$configuration" --nologo

    local scenario_work_root
    scenario_work_root="$(mktemp -d "${TMPDIR:-/tmp}/agent-studio-scenario.XXXXXX")"
    local project_name="agent-studio-scenario-$$"
    local junit="$report_dir/scenario-compose-full.junit.xml"
    local markdown="$report_dir/scenario-compose-full.md"
    local compose_log="$report_dir/scenario-compose-full.compose.log"
    local task_server_port
    local restore_port
    local bff_port
    task_server_port="$(free_port)"
    restore_port="$(free_port)"
    bff_port="$(free_port)"

    (
        export SCENARIO_WORK_ROOT="$scenario_work_root"
        export SCENARIO_TASKSERVER_PORT="$task_server_port"
        export SCENARIO_RESTORE_PORT="$restore_port"
        export STUDIO_TASKSERVER_PORT="$task_server_port"
        export STUDIO_BFF_PORT="$bff_port"
        export DISTRIBUTED_STUDIO_TOKEN="scenario-studio-token-00000000000000000000000000000000"
        export DISTRIBUTED_ENGINE_TOKEN="scenario-engine-token-00000000000000000000000000000000"
        export DISTRIBUTED_RUNNER_TOKEN="scenario-runner-token-00000000000000000000000000000000"
        compose=(
            docker compose
            --project-name "$project_name"
            --file "$repo_root/docker-compose.yml"
            --file "$repo_root/testsupport/scenario/docker-compose.yml"
            --profile distributed
            --profile runner
        )

        finish_compose_full() {
            local status="$?"
            trap - EXIT HUP INT TERM
            set +e
            "${compose[@]}" ps >"$compose_log" 2>&1
            "${compose[@]}" logs --no-color >>"$compose_log" 2>&1
            "${compose[@]}" down --volumes --remove-orphans >/dev/null 2>&1
            case "$scenario_work_root" in
                "${TMPDIR:-/tmp}"/agent-studio-scenario.*) rm -rf -- "$scenario_work_root" ;;
            esac
            if [ "$status" -ne 0 ] && [ ! -f "$junit" ]; then
                cat >"$junit" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<testsuite name="deployment-regression-scenario.compose.full" tests="1" failures="1" skipped="0">
  <testcase classname="deployment-regression-scenario.compose" name="compose-full infrastructure">
    <failure message="compose-full exited $status; see scenario-compose-full.compose.log"/>
  </testcase>
</testsuite>
EOF
                printf '# Deployment regression scenario: compose / full\n\n**Result: FAILED**\n\nCompose infrastructure exited %s. See [compose evidence](scenario-compose-full.compose.log).\n' \
                    "$status" >"$markdown"
            fi
            exit "$status"
        }
        trap finish_compose_full EXIT HUP INT TERM

        "${compose[@]}" config --quiet
        "${compose[@]}" up --build --detach \
            task-server studio-bff scenario-restore-server scenario-runner
        wait_for_http "http://127.0.0.1:${task_server_port}/readyz"
        wait_for_http "http://127.0.0.1:${restore_port}/readyz"
        wait_for_http "http://127.0.0.1:${bff_port}/healthz"

        SCENARIO_TARGET=compose \
        SCENARIO_REPORT_DIR="$report_dir" \
        SCENARIO_EXTERNAL_SERVER_URL="http://127.0.0.1:${task_server_port}" \
        SCENARIO_EXTERNAL_RESTORE_URL="http://127.0.0.1:${restore_port}" \
        SCENARIO_EXTERNAL_WORK_ROOT="$scenario_work_root" \
        SCENARIO_EXTERNAL_RUNNER_ID="scenario-compose-runner" \
        SCENARIO_EXTERNAL_AUTH_TOKEN="$DISTRIBUTED_STUDIO_TOKEN" \
            dotnet test "$repo_root/task-server.Tests/TaskServer.Tests.csproj" \
                --configuration "$configuration" --no-build \
                --filter "FullyQualifiedName~TaskServer.Tests.ScenarioTests.Deployment_regression_scenario_full" \
                --logger "console;verbosity=normal"
    )
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
