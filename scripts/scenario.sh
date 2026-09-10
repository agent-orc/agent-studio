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
           default-profile boot/health checks. --level full starts the
           distributed Task Server and Studio BFF plus the scenario's
           fake-CLI runner image, then drives the same typed steps as inproc.
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
scenario_compose_host_dir=""
scenario_compose_project=""
scenario_compose_file=""
scenario_compose_override=""
scenario_compose_diagnostics_written=0

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
    SCENARIO_TARGET="inproc" SCENARIO_REPORT_DIR="$report_dir" \
      dotnet test "$repo_root/task-server.Tests/TaskServer.Tests.csproj" \
        --configuration "$configuration" --no-build \
        --filter "FullyQualifiedName~TaskServer.Tests.ScenarioTests.$test_name" \
        --logger "console;verbosity=normal"
}

compose_full_cleanup() {
    if [ -n "$scenario_compose_project" ]; then
        docker compose --project-name "$scenario_compose_project" \
            --file "$scenario_compose_file" --file "$scenario_compose_override" \
            --profile distributed --profile runner \
            down --volumes --remove-orphans >/dev/null 2>&1 || true
    fi
    case "$scenario_compose_host_dir" in
        "${TMPDIR:-/tmp}/agent-studio-scenario."*)
            rm -rf -- "$scenario_compose_host_dir"
            ;;
    esac
}

compose_full_diagnostics() {
    if [ -z "$scenario_compose_project" ] || [ "$scenario_compose_diagnostics_written" -eq 1 ]; then
        return
    fi
    scenario_compose_diagnostics_written=1

    local diagnostics="$report_dir/scenario-compose-full.compose.log"
    local compose=(
        docker compose --project-name "$scenario_compose_project"
        --file "$scenario_compose_file" --file "$scenario_compose_override"
        --profile distributed --profile runner
    )
    echo "scenario: capturing Compose diagnostics before cleanup in $diagnostics" >&2
    {
        printf 'Compose service status\n'
        "${compose[@]}" ps --all || true
        printf '\nCompose service logs\n'
        "${compose[@]}" logs --no-color || true
    } >"$diagnostics" 2>&1
    cat "$diagnostics" >&2 || true

    local junit="$report_dir/scenario-compose-full.junit.xml"
    local markdown="$report_dir/scenario-compose-full.md"
    if [ ! -f "$junit" ]; then
        cat >"$junit" <<'EOF'
<?xml version="1.0" encoding="utf-8"?>
<testsuite name="deployment-regression-scenario.compose.full" tests="1" failures="1" skipped="0">
  <testcase classname="deployment-regression-scenario.compose" name="topology-readiness: discover Compose service ports">
    <failure message="Compose target failed before the typed scenario started; see scenario-compose-full.compose.log"/>
  </testcase>
</testsuite>
EOF
    fi
    if [ ! -f "$markdown" ]; then
        cat >"$markdown" <<'EOF'
# Deployment regression scenario: compose / full

**Result: FAILED**

| Step | Status | Duration (s) | Evidence |
|---|---|---|---|
| topology-readiness: Discover Compose service ports | Failed | 0.00 | See [scenario-compose-full.compose.log](scenario-compose-full.compose.log). |
EOF
    fi
}

compose_full_on_exit() {
    local status=$?
    trap - EXIT
    if [ "$status" -ne 0 ]; then
        compose_full_diagnostics
    fi
    compose_full_cleanup
    exit "$status"
}

wait_for_compose_port() {
    local service="$1"
    local container_port="$2"
    local timeout_seconds="${SCENARIO_COMPOSE_PORT_TIMEOUT_SECONDS:-30}"
    case "$timeout_seconds" in
        ''|*[!0-9]*|0)
            echo "error: SCENARIO_COMPOSE_PORT_TIMEOUT_SECONDS must be a positive integer" >&2
            return 2
            ;;
    esac

    local deadline=$((SECONDS + timeout_seconds))
    local binding=""
    local last_error=""
    while :; do
        if binding="$(docker compose --project-name "$scenario_compose_project" \
            --file "$scenario_compose_file" --file "$scenario_compose_override" \
            --profile distributed --profile runner \
            port "$service" "$container_port" 2>&1)" && [ -n "$binding" ]; then
            local host_port="${binding##*:}"
            case "$host_port" in
                ''|*[!0-9]*)
                    last_error="unexpected port binding: $binding"
                    ;;
                *)
                    printf '%s\n' "$binding"
                    return 0
                    ;;
            esac
        else
            last_error="$binding"
        fi

        local container_id
        container_id="$(docker compose --project-name "$scenario_compose_project" \
            --file "$scenario_compose_file" --file "$scenario_compose_override" \
            --profile distributed --profile runner \
            ps --all --quiet "$service" 2>/dev/null || true)"
        container_id="${container_id%%$'\n'*}"
        if [ -n "$container_id" ]; then
            local container_state
            container_state="$(docker inspect --format '{{.State.Status}}' "$container_id" 2>/dev/null || true)"
            case "$container_state" in
                exited|dead)
                    echo "error: Compose service $service entered state $container_state before publishing $container_port/tcp" >&2
                    return 1
                    ;;
            esac
        fi

        if [ "$SECONDS" -ge "$deadline" ]; then
            echo "error: timed out after ${timeout_seconds}s waiting for Compose service $service to publish $container_port/tcp${last_error:+: $last_error}" >&2
            return 1
        fi
        sleep 1
    done
}

run_compose_full() {
    local project_name="${COMPOSE_SCENARIO_PROJECT:-agent-studio-scenario-$$}"
    local build_version
    build_version="$(tr -d '\r\n' < "$repo_root/VERSION")"
    if [ -z "$build_version" ]; then
        echo "VERSION must contain the repository version" >&2
        return 64
    fi
    scenario_compose_host_dir="$(mktemp -d "${TMPDIR:-/tmp}/agent-studio-scenario.XXXXXX")"
    scenario_compose_project="$project_name"
    scenario_compose_file="$repo_root/docker-compose.yml"
    scenario_compose_override="$repo_root/testsupport/scenario/docker-compose.scenario.yml"
    scenario_compose_diagnostics_written=0
    local compose_file="$repo_root/docker-compose.yml"
    local compose_override="$repo_root/testsupport/scenario/docker-compose.scenario.yml"
    local studio_token="scenario-studio-token-000000000000000000000000"
    local engine_token="scenario-engine-token-000000000000000000000000"
    local runner_token="scenario-runner-token-000000000000000000000000"
    local compose=(
        docker compose --project-name "$project_name"
        --file "$compose_file" --file "$compose_override"
        --profile distributed --profile runner
    )

    trap compose_full_on_exit EXIT
    trap 'exit 130' HUP INT TERM

    export SCENARIO_HOST_DIR="$scenario_compose_host_dir"
    export STUDIO_TASKSERVER_PORT=0
    export STUDIO_BFF_PORT=0
    export DISTRIBUTED_STUDIO_TOKEN="$studio_token"
    export DISTRIBUTED_ENGINE_TOKEN="$engine_token"
    export DISTRIBUTED_RUNNER_TOKEN="$runner_token"
    export SCENARIO_BUILD_VERSION="$build_version"
    export SCENARIO_BUILD_SHA="${SCENARIO_BUILD_SHA:-scenario}"
    export SCENARIO_TASK_SERVER_IMAGE="${project_name}-task-server:local"
    export SCENARIO_STUDIO_BFF_IMAGE="${project_name}-studio-bff:local"
    export SCENARIO_AGENT_HOST_IMAGE="${project_name}-agent-host:local"
    export SCENARIO_UID
    SCENARIO_UID="$(id -u)"
    export SCENARIO_GID
    SCENARIO_GID="$(id -g)"
    mkdir -p "$scenario_compose_host_dir/home"
    (
        umask 077
        printf '%s\n' "$runner_token" >"$scenario_compose_host_dir/runner.token"
    )

    echo "scenario: building task-server.Tests ($configuration)..." >&2
    dotnet build "$repo_root/task-server.Tests/TaskServer.Tests.csproj" \
        --configuration "$configuration" --nologo

    echo "scenario: building Compose full target..." >&2
    "${compose[@]}" config --quiet
    "${compose[@]}" build task-server studio-bff agent-host-distributed
    "${compose[@]}" up --detach task-server studio-bff

    local binding
    binding="$(wait_for_compose_port task-server 5071)"
    local target_port="${binding##*:}"
    local bff_binding
    bff_binding="$(wait_for_compose_port studio-bff 5072)"
    local bff_port="${bff_binding##*:}"
    local task_server_image
    task_server_image="$("${compose[@]}" images --quiet task-server)"
    test -n "$task_server_image"

    local test_name="Deployment_regression_scenario_${level}"
    local status=0
    echo "scenario: running $test_name against Compose Task Server on port $target_port..." >&2
    SCENARIO_TARGET="compose" \
    SCENARIO_TARGET_URL="http://127.0.0.1:$target_port" \
    SCENARIO_STUDIO_BFF_URL="http://127.0.0.1:$bff_port" \
    SCENARIO_TARGET_TOKEN="$studio_token" \
    SCENARIO_ENGINE_TOKEN="$engine_token" \
    SCENARIO_COMPOSE_PROJECT="$project_name" \
    SCENARIO_COMPOSE_FILE="$compose_file" \
    SCENARIO_COMPOSE_OVERRIDE_FILE="$compose_override" \
    SCENARIO_TASK_SERVER_IMAGE="$task_server_image" \
    SCENARIO_REPORT_DIR="$report_dir" \
      dotnet test "$repo_root/task-server.Tests/TaskServer.Tests.csproj" \
        --configuration "$configuration" --no-build \
        --filter "FullyQualifiedName~TaskServer.Tests.ScenarioTests.$test_name" \
        --logger "console;verbosity=normal" || status=$?

    if [ "$status" -ne 0 ]; then
        compose_full_diagnostics
    fi

    trap - EXIT HUP INT TERM
    compose_full_cleanup
    return "$status"
}

run_compose() {
    if [ "$level" = "full" ]; then
        run_compose_full
        return
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
