#!/usr/bin/env bash
# Hermetic acceptance check for the container-image release path.
#
# Every scenario below builds from this checkout's Dockerfiles through the
# "dev" profile services (docker-compose.yml), so no registry access is
# required and every commit - not just tagged releases - proves the exact
# Dockerfiles and compose wiring that `docker compose --profile <name> up`
# runs against the published images.
#
#   1. default:      orchestrator-api + frontend (local mode).
#   2. distributed:  task-server + orchestrator-engine + studio-bff, with
#                    orchestrator-api in proxy mode (TaskServer:BaseUrl set).
#   3. runner:       a containerised agent-host registers against the Task
#                    Server and claims a seeded task through to
#                    4-auto-review, using a fake CLI fixture.
#   4. runner-legacy: the plain `--profile runner` path (agent-host-coding
#                    and agent-host-review against the local OrchestratorApi,
#                    no Task Server), bootstrapped only through
#                    scripts/compose-runner-bootstrap.sh - the same artifacts
#                    a first-time operator would generate.
#
# Requires: docker compose v2, curl, jq, git.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
repository_version="$(tr -d '\r\n' < "$repo_root/VERSION")"
if [ -z "$repository_version" ]; then
    echo "VERSION must contain the repository version" >&2
    exit 64
fi
if [ -n "${AGENT_STUDIO_VERSION:-}" ] \
    && [ "$AGENT_STUDIO_VERSION" != "$repository_version" ]; then
    echo "AGENT_STUDIO_VERSION $AGENT_STUDIO_VERSION does not match VERSION $repository_version" >&2
    exit 64
fi
export AGENT_STUDIO_VERSION="$repository_version"

# Kept as a small, Docker-free seam for the version-resolution regression.
if [ "${1:-}" = "--print-build-version" ]; then
    printf '%s\n' "$AGENT_STUDIO_VERSION"
    exit 0
fi
if [ "$#" -ne 0 ]; then
    echo "usage: $0 [--print-build-version]" >&2
    exit 64
fi

project_name="${COMPOSE_SMOKE_PROJECT:-agent-studio-smoke}"
ui_port="${COMPOSE_SMOKE_UI_PORT:-4011}"
api_port="${COMPOSE_SMOKE_API_PORT:-5031}"
taskserver_port="${COMPOSE_SMOKE_TASKSERVER_PORT:-5071}"
bff_port="${COMPOSE_SMOKE_BFF_PORT:-5072}"
compose=(docker compose --project-name "$project_name")
fixture_dir=""
runner_override=""
runner_env_created=""
runner_token_created=""

down()
{
    "${compose[@]}" --profile dev --profile distributed --profile runner \
        down --volumes --remove-orphans >/dev/null 2>&1 || true
}

finish()
{
    status="$1"
    trap - EXIT HUP INT TERM
    if [ "$status" -ne 0 ]; then
        "${compose[@]}" ps || true
        "${compose[@]}" logs --no-color || true
    fi
    down
    [ -n "$fixture_dir" ] && rm -rf "$fixture_dir"
    [ -n "$runner_override" ] && rm -f "$runner_override"
    [ -n "$runner_env_created" ] && rm -f "$repo_root/runner.env"
    [ -n "$runner_token_created" ] && rm -f "$repo_root/runner.token"
    exit "$status"
}

trap 'finish $?' EXIT
trap 'exit 130' HUP INT TERM

healthy_count()
{
    "${compose[@]}" ps --format json | grep -o '"Health":"healthy"' | wc -l | tr -d ' '
}

wait_for_http()
{
    url="$1"
    deadline=$((SECONDS + 60))
    until curl --fail --silent --show-error --output /dev/null "$url" 2>/dev/null; do
        [ "$SECONDS" -lt "$deadline" ] || { echo "timed out waiting for $url" >&2; return 1; }
        sleep 1
    done
}

task_server_call()
{
    method="$1" path="$2" token="$3"
    shift 3
    curl --fail --silent --show-error \
        -X "$method" \
        -H "Authorization: Bearer $token" \
        -H "X-Task-Protocol-Version: 2" \
        -H "Content-Type: application/json" \
        "http://127.0.0.1:${taskserver_port}${path}" \
        "$@"
}

# teardown_scenario <compose-array-name> <profile...> -- <service...>
teardown_scenario()
{
    local -n compose_ref="$1"
    shift
    local -a profiles=()
    while [ "$1" != "--" ]; do
        profiles+=(--profile "$1")
        shift
    done
    shift
    "${compose_ref[@]}" "${profiles[@]}" stop "$@" >/dev/null
    "${compose_ref[@]}" "${profiles[@]}" rm --force "$@" >/dev/null
}

cd "$repo_root"
down

export STUDIO_UI_PORT="$ui_port"
export STUDIO_API_PORT="$api_port"
export STUDIO_TASKSERVER_PORT="$taskserver_port"
export STUDIO_BFF_PORT="$bff_port"
export DISTRIBUTED_STUDIO_TOKEN="${DISTRIBUTED_STUDIO_TOKEN:-smoke-studio-token-0000000000000000000000}"
export DISTRIBUTED_ENGINE_TOKEN="${DISTRIBUTED_ENGINE_TOKEN:-smoke-engine-token-0000000000000000000000}"
export DISTRIBUTED_RUNNER_TOKEN="${DISTRIBUTED_RUNNER_TOKEN:-smoke-runner-token-0000000000000000000000}"

"${compose[@]}" config --quiet

default_services="$("${compose[@]}" config --services)"
test "$default_services" = "$(printf 'orchestrator-api\nfrontend')"

# --- Scenario 1: default two-service path (published-image topology) -----
echo "=== default profile ==="
"${compose[@]}" --profile dev up --build --wait orchestrator-api-dev frontend-dev

ui_binding="$("${compose[@]}" port frontend-dev 8080)"
api_binding="$("${compose[@]}" port orchestrator-api-dev 5031)"
resolved_ui_port="${ui_binding##*:}"
resolved_api_port="${api_binding##*:}"

health="$(curl --fail --silent "http://127.0.0.1:${resolved_ui_port}/healthz")"
test "$health" = '"ok"'

homepage="$(curl --fail --silent "http://127.0.0.1:${resolved_ui_port}/")"
grep -q '<app-root' <<<"$homepage"

tasks="$(curl --fail --silent "http://127.0.0.1:${resolved_ui_port}/api/tasks/grouped")"
grep -q '"backlog"' <<<"$tasks"

test "$(healthy_count)" -eq 2

teardown_scenario compose dev -- orchestrator-api-dev frontend-dev

printf '%s\n' \
    "compose-smoke=passed" \
    "scenario=default" \
    "services=orchestrator-api,frontend" \
    "health=$health" \
    "browser-shell=app-root" \
    "api-tasks-grouped=json" \
    "ui-port=$resolved_ui_port" \
    "api-port=$resolved_api_port"

# --- Scenario 2: distributed profile, OrchestratorApi in proxy mode ------
echo "=== distributed profile ==="
export TASK_SERVER_BASE_URL="http://task-server-dev:5071"
"${compose[@]}" --profile dev --profile distributed up --build --wait \
    task-server-dev orchestrator-engine-dev studio-bff-dev orchestrator-api-dev

test "$(healthy_count)" -eq 4   # task-server-dev, orchestrator-engine-dev, studio-bff-dev, orchestrator-api-dev

direct_protocol="$(curl --fail --silent "http://127.0.0.1:${taskserver_port}/api/v1/protocol")"
api_binding="$("${compose[@]}" port orchestrator-api-dev 5031)"
resolved_api_port="${api_binding##*:}"
proxied_protocol="$(curl --fail --silent "http://127.0.0.1:${resolved_api_port}/api/v1/protocol")"
test "$proxied_protocol" = "$direct_protocol"

bff_binding="$("${compose[@]}" port studio-bff-dev 5072)"
resolved_bff_port="${bff_binding##*:}"
bff_health="$(curl --fail --silent "http://127.0.0.1:${resolved_bff_port}/healthz")"
grep -q '"status":"live"' <<<"$bff_health"

printf '%s\n' \
    "compose-smoke=passed" \
    "scenario=distributed" \
    "services=task-server,orchestrator-engine,studio-bff,orchestrator-api(proxy)" \
    "protocol-proxy=matched" \
    "bff-health=$bff_health"

teardown_scenario compose dev distributed -- \
    task-server-dev orchestrator-engine-dev studio-bff-dev orchestrator-api-dev
unset TASK_SERVER_BASE_URL

# --- Scenario 3: agent-host registers against the Task Server and --------
#     claims a seeded task through to 4-auto-review, via a fake CLI.
echo "=== runner (agent-host <-> Task Server) profile ==="
fixture_dir="$(mktemp -d)"
smoke_uid="$(id -u)"
smoke_gid="$(id -g)"
if [ "$smoke_uid" -eq 0 ]; then
    echo "compose smoke must run as a non-root host user" >&2
    exit 64
fi
bare_repo="$fixture_dir/origin.git"
seed_repo="$fixture_dir/seed"
git init --quiet --bare "$bare_repo"
git init --quiet -b main "$seed_repo"
printf 'compose smoke fixture\n' > "$seed_repo/README.md"
git -C "$seed_repo" -c user.name="Compose Smoke" -c user.email="smoke@example.invalid" add .
git -C "$seed_repo" -c user.name="Compose Smoke" -c user.email="smoke@example.invalid" commit --quiet -m fixture
git -C "$seed_repo" remote add origin "$bare_repo"
git -C "$seed_repo" push --quiet -u origin main
git -C "$bare_repo" symbolic-ref HEAD refs/heads/main
mkdir -p "$fixture_dir/home" "$fixture_dir/runner-work" "$fixture_dir/state"

cat > "$fixture_dir/topology-agent.sh" <<'FAKE_CLI'
#!/bin/sh
set -eu
if [ "${1:-}" = "--version" ]; then
  printf 'topology-agent 1.0.0\n'
  exit 0
fi
mkdir -p "$JOB_RESULTS_DIR"
printf 'compose smoke artifact\n' > "$JOB_RESULTS_DIR/proof.txt"
printf '{"type":"agent_message","text":"compose smoke run complete"}\n'
printf '{"type":"tool","name":"fixture-tool"}\n'
printf '[[TASK_DONE]]\n'
FAKE_CLI
chmod +x "$fixture_dir/topology-agent.sh"

runner_override="$(mktemp)"
cat > "$runner_override" <<OVERRIDE
services:
  agent-host-distributed-dev:
    user: "$smoke_uid:$smoke_gid"
    volumes:
      - $fixture_dir:/fixtures
    secrets:
      - source: runner_token
        uid: "$smoke_uid"
        gid: "$smoke_gid"
        mode: 0400
    environment:
      HOME: /fixtures/home
      RUNNER_WORKDIR: /fixtures/runner-work
      RUNNER_STATE_DIR: /fixtures/state
      RUNNER_GIT_REMOTE: file:///fixtures/origin.git
      RUNNER_GIT_PUSH_REMOTE: file:///fixtures/origin.git
      RUNNER_CLI_BIN: /fixtures/topology-agent.sh
      RUNNER_TTL_SECONDS: "60"
      RUNNER_MAX_PARALLELISM: "1"
      RUNNER_POLL_SECONDS: "1"
OVERRIDE

compose_r3=(docker compose --project-name "$project_name" -f docker-compose.yml -f "$runner_override")
"${compose_r3[@]}" --profile dev up --build --wait task-server-dev
wait_for_http "http://127.0.0.1:${taskserver_port}/readyz"

workspace="$(task_server_call POST /api/v1/workspaces "$DISTRIBUTED_STUDIO_TOKEN" \
    -d '{"name":"Compose smoke"}')"
workspace_id="$(jq -r '.workspaceId' <<<"$workspace")"

project="$(task_server_call POST /api/v1/projects "$DISTRIBUTED_STUDIO_TOKEN" \
    -d "$(jq -n --arg ws "$workspace_id" '{workspaceId: $ws, name: "Agent Studio", taskKeyPrefix: "SMK"}')")"
project_id="$(jq -r '.projectId' <<<"$project")"

task="$(task_server_call POST "/api/v1/projects/$project_id/tasks" "$DISTRIBUTED_STUDIO_TOKEN" \
    -d '{"title":"Compose smoke task","body":"Prove agent-host claims and completes.","state":"2-ready"}')"
task_key="$(jq -r '.taskKey' <<<"$task")"

"${compose_r3[@]}" --profile dev up --build --wait agent-host-distributed-dev

deadline=$((SECONDS + 60))
task_state=""
until [ "$task_state" = "4-auto-review" ]; do
    history="$(task_server_call GET "/api/v1/projects/$project_id/tasks/$task_key/history" "$DISTRIBUTED_STUDIO_TOKEN")"
    task_state="$(jq -r '.task.state' <<<"$history")"
    [ "$SECONDS" -lt "$deadline" ] || {
        echo "task $task_key did not reach 4-auto-review (last state: $task_state)" >&2
        exit 1
    }
    [ "$task_state" = "4-auto-review" ] || sleep 2
done

printf '%s\n' \
    "compose-smoke=passed" \
    "scenario=runner" \
    "task-key=$task_key" \
    "task-state=$task_state"

teardown_scenario compose_r3 dev -- task-server-dev agent-host-distributed-dev

# --- Scenario 4: the plain `runner` profile, bootstrapped exactly the way --
#     a first-time operator would (scripts/compose-runner-bootstrap.sh),
#     against the local OrchestratorApi rather than a Task Server.
echo "=== runner (agent-host <-> OrchestratorApi) profile ==="
[ -e "$repo_root/runner.env" ] || runner_env_created=1
[ -e "$repo_root/runner.token" ] || runner_token_created=1
"$repo_root/scripts/compose-runner-bootstrap.sh" >/dev/null

"${compose[@]}" --profile dev up --build --wait \
    orchestrator-api-dev agent-host-coding-dev agent-host-review-dev

test "$(healthy_count)" -eq 3   # orchestrator-api-dev, agent-host-coding-dev, agent-host-review-dev

printf '%s\n' \
    "compose-smoke=passed" \
    "scenario=runner-legacy" \
    "services=orchestrator-api,agent-host-coding,agent-host-review"

teardown_scenario compose dev -- \
    orchestrator-api-dev agent-host-coding-dev agent-host-review-dev

if [ -n "$runner_env_created" ]; then
    rm -f "$repo_root/runner.env"
    runner_env_created=""
fi
if [ -n "$runner_token_created" ]; then
    rm -f "$repo_root/runner.token"
    runner_token_created=""
fi
