#!/usr/bin/env bash
# Hermetic acceptance check for the Docker Compose paths that ship as product:
#   1. the default new-user stack (orchestrator API + frontend);
#   2. the distributed target-architecture stack (task-server, orchestrator
#      engine, studio-bff, with OrchestratorApi in /api/v1 proxy mode).
#
# The stack runs published images, so this script first builds exactly the tags
# the services reference. An unreleased commit has no image in the registry.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project_name="${COMPOSE_SMOKE_PROJECT:-agent-studio-smoke}"
ui_port="${COMPOSE_SMOKE_UI_PORT:-4011}"
api_port="${COMPOSE_SMOKE_API_PORT:-5031}"
taskserver_port="${COMPOSE_SMOKE_TASKSERVER_PORT:-5071}"
bff_port="${COMPOSE_SMOKE_BFF_PORT:-5072}"
compose=(docker compose --project-name "$project_name")
# Diagnostics and teardown must see every profile this script starts, otherwise
# a failure in the distributed run reports an empty container list. The runner
# profile is deliberately absent: its env_file is `required: true`, so naming it
# would make teardown fail on a machine without runner.env.
all_profiles=(--profile dev --profile distributed)

down()
{
    "${compose[@]}" "${all_profiles[@]}" down --volumes --remove-orphans >/dev/null 2>&1 || true
}

finish()
{
    status="$1"
    trap - EXIT HUP INT TERM
    if [ "$status" -ne 0 ]; then
        "${compose[@]}" "${all_profiles[@]}" ps || true
        "${compose[@]}" "${all_profiles[@]}" logs --no-color || true
    fi
    down
    exit "$status"
}

trap 'finish $?' EXIT
trap 'exit 130' HUP INT TERM

cd "$repo_root"
down

export STUDIO_UI_PORT="$ui_port"
export STUDIO_API_PORT="$api_port"
export STUDIO_TASKSERVER_PORT="$taskserver_port"
export STUDIO_BFF_PORT="$bff_port"
# A tag of its own so a local build never overwrites a real release tag that
# happens to be present on the machine.
export AGENT_STUDIO_VERSION="${COMPOSE_SMOKE_IMAGE_TAG:-smoke-local}"

"${compose[@]}" config --quiet

# --- Build the image tags the services reference ----------------------------
# `build:` lives only in the dev profile, so this is the one command that turns
# the checkout into images. Everything after this point runs from images.
"${compose[@]}" --profile dev build

for image in agent-studio-api agent-studio-web agent-task-server \
             agent-orchestrator-engine agent-studio-bff agent-host; do
    reference="ghcr.io/agent-orc/$image:$AGENT_STUDIO_VERSION"
    user="$(docker image inspect --format '{{.Config.User}}' "$reference")"
    test -n "$user"
    test "$user" != "root"
    test "$user" != "0"
    test -n "$(docker image inspect --format '{{if .Config.Healthcheck}}set{{end}}' "$reference")"
    printf 'image=%s user=%s healthcheck=declared\n' "$reference" "$user"
done

# --- Run 1: default profile -------------------------------------------------
services="$("${compose[@]}" config --services)"
test "$services" = "$(printf 'orchestrator-api\nfrontend')"

"${compose[@]}" up --wait

ui_binding="$("${compose[@]}" port frontend 8080)"
api_binding="$("${compose[@]}" port orchestrator-api 5031)"
resolved_ui_port="${ui_binding##*:}"
resolved_api_port="${api_binding##*:}"

health="$(curl --fail --silent "http://127.0.0.1:${resolved_ui_port}/healthz")"
test "$health" = '"ok"'

homepage="$(curl --fail --silent "http://127.0.0.1:${resolved_ui_port}/")"
grep -q '<app-root' <<<"$homepage"

tasks="$(curl --fail --silent "http://127.0.0.1:${resolved_ui_port}/api/tasks/grouped")"
grep -q '"backlog"' <<<"$tasks"

status="$("${compose[@]}" ps --format json)"
test "$(grep -o '"Health":"healthy"' <<<"$status" | wc -l)" -eq 2

printf '%s\n' \
    "compose-smoke=passed" \
    "project=$project_name" \
    "services=orchestrator-api,frontend" \
    "health=$health" \
    "browser-shell=app-root" \
    "api-tasks-grouped=json" \
    "ui-port=$resolved_ui_port" \
    "api-port=$resolved_api_port"

down

# --- Run 2: distributed profile --------------------------------------------
# The Task Server's LISTEN_URL is not loopback inside the container network, so
# it refuses AUTH=none. Engine, Studio BFF, and the OrchestratorApi proxy all
# authenticate with this same ephemeral credential.
if command -v openssl >/dev/null 2>&1; then
    AGENT_STUDIO_TASKSERVER_TOKEN="$(openssl rand -hex 32)"
else
    AGENT_STUDIO_TASKSERVER_TOKEN="$(head -c 32 /dev/urandom | od -An -tx1 | tr -d ' \n')"
fi
export AGENT_STUDIO_TASKSERVER_TOKEN
test "${#AGENT_STUDIO_TASKSERVER_TOKEN}" -ge 32
# Turns OrchestratorApi into a transparent proxy: it stops mapping local v1
# routes and forwards /api/v1 to the standalone Task Server.
export AGENT_STUDIO_TASKSERVER_BASEURL="http://task-server:5071"

distributed_services="$("${compose[@]}" --profile distributed config --services | sort)"
test "$distributed_services" = "$(printf 'frontend\norchestrator-api\norchestrator-engine\nstudio-bff\ntask-server')"

"${compose[@]}" --profile distributed up --wait

taskserver_binding="$("${compose[@]}" port task-server 5071)"
bff_binding="$("${compose[@]}" port studio-bff 5072)"
api_binding="$("${compose[@]}" port orchestrator-api 5031)"
resolved_taskserver_port="${taskserver_binding##*:}"
resolved_bff_port="${bff_binding##*:}"
resolved_api_port="${api_binding##*:}"

ready="$(curl --fail --silent "http://127.0.0.1:${resolved_taskserver_port}/readyz")"
grep -q '"status":"ready"' <<<"$ready"

bff_health="$(curl --fail --silent "http://127.0.0.1:${resolved_bff_port}/healthz")"
grep -q '"role":"studio-bff"' <<<"$bff_health"

# The proxy assertion: /api/v1 through OrchestratorApi must be answered by the
# same Task Server process the direct port reaches. Identical serverId is what
# separates a real proxy from a local v1 implementation still being mapped.
# GET /api/v1/protocol is open by contract, so this needs no credential.
direct_protocol="$(curl --fail --silent "http://127.0.0.1:${resolved_taskserver_port}/api/v1/protocol")"
proxied_protocol="$(curl --fail --silent "http://127.0.0.1:${resolved_api_port}/api/v1/protocol")"
direct_server_id="$(grep -o '"serverId":"[^"]*"' <<<"$direct_protocol")"
proxied_server_id="$(grep -o '"serverId":"[^"]*"' <<<"$proxied_protocol")"
test -n "$direct_server_id"
test "$direct_server_id" = "$proxied_server_id"

# Five services, every one of them reporting healthy through its HEALTHCHECK.
# orchestrator-engine and agent-host serve no HTTP port; their check is the
# `--health-check` verb probing the Task Server.
status="$("${compose[@]}" --profile distributed ps --format json)"
test "$(grep -o '"Health":"healthy"' <<<"$status" | wc -l)" -eq 5

printf '%s\n' \
    "compose-smoke-distributed=passed" \
    "services=orchestrator-api,frontend,task-server,orchestrator-engine,studio-bff" \
    "task-server-readyz=ready" \
    "studio-bff=$bff_health" \
    "proxy-server-id=$direct_server_id" \
    "task-server-port=$resolved_taskserver_port" \
    "bff-port=$resolved_bff_port" \
    "api-port=$resolved_api_port"
