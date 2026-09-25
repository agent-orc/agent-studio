#!/usr/bin/env bash
# Prove that a Task Server store created by release N-1 survives release N.
set -euo pipefail
if [ "$#" -ne 2 ]; then
    echo "usage: $0 v<previous-version> v<current-version>" >&2
    exit 64
fi
previous="$1"
current="$2"
project="${COMPOSE_UPGRADE_PROJECT:-agent-studio-upgrade-smoke}"
compose=(docker compose --project-name "$project")
finish() {
    status="$?"
    trap - EXIT
    if [ "$status" -ne 0 ]; then
        "${compose[@]}" ps || true
        "${compose[@]}" logs --tail 80 --no-color || true
    fi
    "${compose[@]}" down --volumes --remove-orphans >/dev/null 2>&1 || true
    exit "$status"
}
trap finish EXIT
trap 'exit 130' HUP INT TERM

cd "$(dirname "$0")/.."
"${compose[@]}" down --volumes --remove-orphans >/dev/null 2>&1 || true
export AGENT_STUDIO_VERSION="$previous"
"${compose[@]}" up -d --wait bootstrap task-server
studio_token="$("${compose[@]}" exec -T task-server cat /run/secrets/studio_token)"
request() {
    curl -fsS -H "Authorization: Bearer $studio_token" \
        -H 'X-Task-Protocol-Version: 2' -H 'Content-Type: application/json' \
        "http://127.0.0.1:${STUDIO_TASKSERVER_PORT:-5071}$1" "${@:2}"
}
workspace="$(request /api/v1/workspaces -X POST -d '{"name":"Upgrade retained workspace"}')"
workspace_id="$(jq -r .workspaceId <<<"$workspace")"
"${compose[@]}" down
export AGENT_STUDIO_VERSION="$current"
"${compose[@]}" up -d --wait
request /api/v1/workspaces | jq -e --arg id "$workspace_id" 'any(.[]; .workspaceId == $id)' >/dev/null
stored_token="$("${compose[@]}" exec -T task-server cat /run/secrets/studio_token)"
test "$studio_token" = "$stored_token"
echo "compose-upgrade=passed previous=$previous current=$current workspace=$workspace_id"
