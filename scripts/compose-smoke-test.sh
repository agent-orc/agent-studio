#!/usr/bin/env bash
# Hermetic acceptance check for the container-image release path.
#
# Every scenario below builds from this checkout's Dockerfiles through the
# "dev" profile services (docker-compose.yml), so no registry access is
# required and every commit - not just tagged releases - proves the exact
# Dockerfiles and compose wiring that `docker compose --profile <name> up`
# runs against the published images.
#
#   1. default:      task-server + engine + BFF + frontend (one authority).
#   2. compatibility: versioned proxy forwards while legacy writes fail closed.
#   3. runner:       a containerised agent-host registers against the Task
#                    Server and claims a seeded task through to
#                    4-auto-review, using a fake CLI fixture.
# The separate legacy-local runner path remains owned by the accepted option C
# baseline and is not a service in this one-box installation check.
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

down()
{
    "${compose[@]}" --profile dev --profile legacy \
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
export DISTRIBUTED_REVIEW_RUNNER_TOKEN="${DISTRIBUTED_REVIEW_RUNNER_TOKEN:-smoke-review-token-0000000000000000000000}"
export STUDIO_ALLOWED_ORIGINS="http://127.0.0.1:${ui_port}"

"${compose[@]}" config --quiet

default_services="$("${compose[@]}" config --services | sort)"
test "$default_services" = "$(printf 'agent-host-distributed\nagent-host-review-distributed\nfrontend\norchestrator-engine\nstudio-bff\ntask-server')"

# --- Scenario 1: source-built one-box authority and browser boundary -------
echo "=== default profile ==="
"${compose[@]}" --profile dev up --build --wait \
    task-server-dev orchestrator-engine-dev studio-bff-dev frontend-dev

ui_binding="$("${compose[@]}" port frontend-dev 8080)"
resolved_ui_port="${ui_binding##*:}"

health="$(curl --fail --silent "http://127.0.0.1:${resolved_ui_port}/healthz")"
grep -q '"status":"live"' <<<"$health"

homepage="$(curl --fail --silent "http://127.0.0.1:${resolved_ui_port}/")"
grep -q '<app-root' <<<"$homepage"

direct_protocol="$(task_server_call GET /api/v1/protocol "$DISTRIBUTED_STUDIO_TOKEN")"
edge_protocol="$(curl --fail --silent "http://127.0.0.1:${resolved_ui_port}/api/v1/protocol")"
test "$edge_protocol" = "$direct_protocol"
unauthenticated_status="$(curl --silent --output /dev/null --write-out '%{http_code}' \
    "http://127.0.0.1:${taskserver_port}/api/v1/workspaces")"
test "$unauthenticated_status" = 401

no_origin_status="$(curl --silent --output /dev/null --write-out '%{http_code}' \
    -X POST -H 'Content-Type: application/json' -d '{"name":"rejected"}' \
    "http://127.0.0.1:${resolved_ui_port}/api/v1/workspaces")"
test "$no_origin_status" = 403
foreign_origin_status="$(curl --silent --output /dev/null --write-out '%{http_code}' \
    -X POST -H 'Origin: https://foreign.invalid' -H 'Content-Type: application/json' \
    -d '{"name":"rejected"}' "http://127.0.0.1:${resolved_ui_port}/api/v1/workspaces")"
test "$foreign_origin_status" = 403
unknown_status="$(curl --silent --output /dev/null --write-out '%{http_code}' \
    "http://127.0.0.1:${resolved_ui_port}/api/not-owned")"
test "$unknown_status" = 404
created_workspace="$(curl --fail --silent -X POST \
    -H "Origin: http://127.0.0.1:${resolved_ui_port}" \
    -H 'Content-Type: application/json' -d '{"name":"Browser smoke"}' \
    "http://127.0.0.1:${resolved_ui_port}/api/v1/workspaces")"
workspace_id="$(jq -r '.workspaceId' <<<"$created_workspace")"
test -n "$workspace_id" && test "$workspace_id" != null
task_server_call GET /api/v1/workspaces "$DISTRIBUTED_STUDIO_TOKEN" | jq -e --arg id "$workspace_id" \
    '.[] | select(.workspaceId == $id)' >/dev/null
principal_ids_before="$(task_server_call GET /api/v1/management/principals "$DISTRIBUTED_STUDIO_TOKEN" \
    | jq -r '.[].principalId' | sort)"
"${compose[@]}" restart task-server-dev >/dev/null
wait_for_http "http://127.0.0.1:${taskserver_port}/readyz"
task_server_call GET /api/v1/workspaces "$DISTRIBUTED_STUDIO_TOKEN" | jq -e --arg id "$workspace_id" \
    '.[] | select(.workspaceId == $id)' >/dev/null
principal_ids_after="$(task_server_call GET /api/v1/management/principals "$DISTRIBUTED_STUDIO_TOKEN" \
    | jq -r '.[].principalId' | sort)"
test "$principal_ids_before" = "$principal_ids_after"
printf 'checkpoint=principal-ids-preserved\n'

deadline=$((SECONDS + 30))
until [ "$(healthy_count)" -eq 4 ]; do
    [ "$SECONDS" -lt "$deadline" ] || { echo 'one-box services did not recover health after restart' >&2; exit 1; }
    sleep 1
done

teardown_scenario compose dev -- task-server-dev orchestrator-engine-dev studio-bff-dev frontend-dev

printf '%s\n' \
    "compose-smoke=passed" \
    "scenario=default" \
    "services=task-server,orchestrator-engine,studio-bff,frontend" \
    "health=$health" \
    "browser-shell=app-root" \
    "browser-mutation=task-server:$workspace_id" \
    "unknown-and-foreign-origin=closed" \
    "direct-unauthenticated=closed" \
    "restart=workspace-and-principals-preserved" \
    "ui-port=$resolved_ui_port" \
    "task-server-port=$taskserver_port"

# --- Scenario 2: compatibility proxy must not write its local store -------
echo "=== compatibility proxy ==="
export TASK_SERVER_BASE_URL="http://task-server-dev:5071"
"${compose[@]}" --profile dev up --build --wait \
    task-server-dev orchestrator-api-dev

test "$(healthy_count)" -eq 2

direct_protocol="$(curl --fail --silent "http://127.0.0.1:${taskserver_port}/api/v1/protocol")"
api_binding="$("${compose[@]}" port orchestrator-api-dev 5031)"
resolved_api_port="${api_binding##*:}"
proxied_protocol="$(curl --fail --silent "http://127.0.0.1:${resolved_api_port}/api/v1/protocol")"
test "$proxied_protocol" = "$direct_protocol"

legacy_write_status="$(curl --silent --output /dev/null --write-out '%{http_code}' \
    -X POST -H 'Content-Type: application/json' -d '{}' \
    "http://127.0.0.1:${resolved_api_port}/api/projects")"
test "$legacy_write_status" = 404

printf '%s\n' \
    "compose-smoke=passed" \
    "scenario=compatibility" \
    "services=task-server,orchestrator-api(proxy)" \
    "protocol-proxy=matched" \
    "legacy-write=closed"

teardown_scenario compose dev -- task-server-dev orchestrator-api-dev
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

"${compose_r3[@]}" --profile dev build agent-host-distributed-dev
"${compose_r3[@]}" --profile dev up --wait --no-deps agent-host-distributed-dev

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

"${compose_r3[@]}" --profile dev build agent-host-review-distributed-dev
"${compose_r3[@]}" --profile dev up --wait --no-deps agent-host-review-distributed-dev
deadline=$((SECONDS + 30))
until task_server_call GET /api/v1/management/remote-hosts "$DISTRIBUTED_STUDIO_TOKEN" \
    | jq -e '([.[].runnerId] | index("distributed-runner") != null and index("distributed-review-runner") != null)' >/dev/null; do
    [ "$SECONDS" -lt "$deadline" ] || { echo 'coding and review registrations did not become visible' >&2; exit 1; }
    sleep 1
done

printf '%s\n' \
    "compose-smoke=passed" \
    "scenario=runner" \
    "task-key=$task_key" \
    "task-state=$task_state" \
    "runner-roles=coding,review"

teardown_scenario compose_r3 dev -- task-server-dev agent-host-distributed-dev agent-host-review-distributed-dev
