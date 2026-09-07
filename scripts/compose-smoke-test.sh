#!/usr/bin/env bash
# Three-phase acceptance check for release images (release CI) or the explicit
# dev-profile source-build override (branch CI).
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project_name="${COMPOSE_SMOKE_PROJECT:-agent-studio-smoke}"
runtime_root="$(mktemp -d /tmp/agent-studio-compose-smoke.XXXXXX)"
use_images="${COMPOSE_SMOKE_USE_IMAGES:-0}"

export AGENT_STUDIO_VERSION="${AGENT_STUDIO_VERSION:-dev}"
export STUDIO_UI_PORT="${COMPOSE_SMOKE_UI_PORT:-4011}"
export STUDIO_API_PORT="${COMPOSE_SMOKE_API_PORT:-5031}"
export STUDIO_PROXY_API_PORT="${COMPOSE_SMOKE_PROXY_API_PORT:-5032}"
export STUDIO_TASKSERVER_PORT="${COMPOSE_SMOKE_TASKSERVER_PORT:-5071}"
export STUDIO_BFF_PORT="${COMPOSE_SMOKE_BFF_PORT:-5072}"
export STUDIO_WORKSPACE_PATH="$runtime_root/workspace"
export STUDIO_PROJECTS_PATH="$runtime_root/projects"
export TASK_SERVER_DATA_PATH="$runtime_root/task-server"
export RUNNER_STATE_PATH="$runtime_root/agent-host"
export RUNNER_FIXTURE_PATH="$runtime_root/fixtures"
export TASK_SERVER_AUTH_TOKEN="compose-smoke-token-0123456789abcdef"
export TASK_SERVER_MINIMUM_LEASE_SECONDS=5
export TASK_SERVER_MAXIMUM_LEASE_SECONDS=30
export TASK_SERVER_MAXIMUM_EVENT_PAYLOAD_BYTES=262144
export ORCHESTRATOR_ENGINE_CLIENT_ID=compose-smoke-engine
export ORCHESTRATOR_REVIEW_CONCURRENCY=1
export ORCHESTRATOR_COUNCIL_CONCURRENCY=1
export ORCHESTRATOR_POST_PROCESSING_CONCURRENCY=1
export ORCHESTRATOR_GATE_DISPATCH_CONCURRENCY=1
export ORCHESTRATOR_COMPLETION_JUDGE_CONCURRENCY=1
export ORCHESTRATOR_POLL_SECONDS=1
export ORCHESTRATOR_LEASE_SECONDS=30
export RUNNER_GIT_REMOTE=file:///fixtures/origin.git
export RUNNER_GIT_PUSH_REMOTE=file:///fixtures/origin.git
export RUNNER_MAX_PARALLELISM=1
export RUNNER_CLI_BIN=/fixtures/fake-cli
export CONTAINER_LOG_MAX_SIZE=1m
export CONTAINER_LOG_MAX_FILES=2

compose=(docker compose --project-name "$project_name" -f docker-compose.yml -f scripts/compose-smoke.override.yml)
up_args=(--wait)
if [ "$use_images" != 1 ]; then
    compose+=(-f docker-compose.dev.yml --profile dev)
    up_args+=(--build)
fi

down()
{
    "${compose[@]}" down --volumes --remove-orphans >/dev/null 2>&1 || true
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
    case "$runtime_root" in
        /tmp/agent-studio-compose-smoke.*) rm -rf "$runtime_root" ;;
    esac
    exit "$status"
}

trap 'finish $?' EXIT
trap 'exit 130' HUP INT TERM

cd "$repo_root"
mkdir -p "$STUDIO_WORKSPACE_PATH" "$STUDIO_PROJECTS_PATH" "$TASK_SERVER_DATA_PATH" \
    "$RUNNER_STATE_PATH" "$RUNNER_FIXTURE_PATH"
chmod -R a+rwX "$runtime_root"
down

"${compose[@]}" config --quiet
services="$("${compose[@]}" config --services)"
test "$services" = "$(printf 'orchestrator-api\nfrontend')"
"${compose[@]}" up "${up_args[@]}"

ui_binding="$("${compose[@]}" port frontend 8080)"
api_binding="$("${compose[@]}" port orchestrator-api 5031)"
resolved_ui_port="${ui_binding##*:}"
resolved_api_port="${api_binding##*:}"
health="$(curl --fail --silent "http://127.0.0.1:${resolved_ui_port}/healthz")"
test "$health" = '"ok"'
curl --fail --silent "http://127.0.0.1:${resolved_ui_port}/" | grep -q '<app-root'
curl --fail --silent "http://127.0.0.1:${resolved_ui_port}/api/tasks/grouped" | grep -q '"backlog"'
test "$("${compose[@]}" ps --format json | grep -o '"Health":"healthy"' | wc -l)" -eq 2
down

# Distributed profile: Task Server is the sole /api/v1 owner and the legacy API
# proves proxy mode by returning its protocol document from that origin.
"${compose[@]}" --profile distributed up "${up_args[@]}"
auth=(-H "Authorization: Bearer $TASK_SERVER_AUTH_TOKEN")
protocol_direct="$(curl --fail --silent "${auth[@]}" "http://127.0.0.1:${STUDIO_TASKSERVER_PORT}/api/v1/protocol")"
protocol_proxy="$(curl --fail --silent "${auth[@]}" "http://127.0.0.1:${STUDIO_PROXY_API_PORT}/api/v1/protocol")"
protocol_bff="$(curl --fail --silent "http://127.0.0.1:${STUDIO_BFF_PORT}/api/v1/protocol")"
test "$protocol_proxy" = "$protocol_direct"
test "$protocol_bff" = "$protocol_direct"
for service in task-server orchestrator-api-proxy orchestrator-engine studio-bff; do
    test "$("${compose[@]}" ps "$service" --format json | grep -o '"Health":"healthy"' | wc -l)" -eq 1
done
down

# Runner profile: a real agent-host container registers, claims a seeded task,
# runs the deterministic fake CLI, and completes it into Auto Review.
git init --bare "$RUNNER_FIXTURE_PATH/origin.git" >/dev/null
seed="$runtime_root/seed"
git init -b main "$seed" >/dev/null
printf 'compose runner fixture\n' > "$seed/README.md"
git -C "$seed" -c user.name='Compose Smoke' -c user.email=compose@example.invalid add README.md
git -C "$seed" -c user.name='Compose Smoke' -c user.email=compose@example.invalid commit -m fixture >/dev/null
git -C "$seed" remote add origin "$RUNNER_FIXTURE_PATH/origin.git"
git -C "$seed" push -u origin main >/dev/null
git -C "$RUNNER_FIXTURE_PATH/origin.git" symbolic-ref HEAD refs/heads/main
cp task-server.Tests/Fixtures/topology-fake-cli.sh "$RUNNER_FIXTURE_PATH/fake-cli"
chmod 0555 "$RUNNER_FIXTURE_PATH/fake-cli"
chmod -R a+rwX "$RUNNER_FIXTURE_PATH" "$RUNNER_STATE_PATH"

"${compose[@]}" --profile runner up "${up_args[@]}"
api="http://127.0.0.1:${STUDIO_TASKSERVER_PORT}/api/v1"
headers=(-H "Authorization: Bearer $TASK_SERVER_AUTH_TOKEN" -H 'X-Task-Protocol-Version: 2' -H 'Content-Type: application/json')
workspace="$(curl --fail --silent "${headers[@]}" -d '{"name":"Compose smoke"}' "$api/workspaces")"
workspace_id="$(python3 -c 'import json,sys; print(json.load(sys.stdin)["workspaceId"])' <<<"$workspace")"
project="$(curl --fail --silent "${headers[@]}" -d "{\"workspaceId\":\"$workspace_id\",\"name\":\"Compose smoke\",\"taskKeyPrefix\":\"CSM\"}" "$api/projects")"
project_id="$(python3 -c 'import json,sys; print(json.load(sys.stdin)["projectId"])' <<<"$project")"
task="$(curl --fail --silent "${headers[@]}" -d '{"title":"Container runner claim proof","body":"Finish through Auto Review.","state":"2-ready"}' "$api/projects/$project_id/tasks")"
task_key="$(python3 -c 'import json,sys; print(json.load(sys.stdin)["taskKey"])' <<<"$task")"

deadline=$((SECONDS + 90))
state=""
while [ "$SECONDS" -lt "$deadline" ]; do
    task="$(curl --fail --silent "${headers[@]}" "$api/projects/$project_id/tasks/$task_key")"
    state="$(python3 -c 'import json,sys; print(json.load(sys.stdin)["state"])' <<<"$task")"
    [ "$state" = 4-auto-review ] && break
    sleep 1
done
test "$state" = 4-auto-review
runners="$(curl --fail --silent "${headers[@]}" "$api/runners")"
python3 -c 'import json,sys; assert len(json.load(sys.stdin)) >= 1' <<<"$runners"

printf '%s\n' \
    'compose-smoke=passed' \
    'default=orchestrator-api,frontend' \
    'distributed=task-server,orchestrator-engine,studio-bff,orchestrator-api-proxy' \
    "proxy-protocol=matched" \
    "runner-task=$task_key:$state" \
    "ui-port=$resolved_ui_port" \
    "api-port=$resolved_api_port"
