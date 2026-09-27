#!/usr/bin/env bash
# Release CI follow-up: prove an N-1 published-image store survives N.
set -euo pipefail
previous="${PREVIOUS_VERSION:?set PREVIOUS_VERSION without v}"
current="${CURRENT_VERSION:?set CURRENT_VERSION without v}"
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="${COMPOSE_UPGRADE_PROJECT:-agent-studio-upgrade-$$}"
export STUDIO_UI_PORT="${COMPOSE_UPGRADE_UI_PORT:-14011}"
export STUDIO_TASKSERVER_PORT="${COMPOSE_UPGRADE_TASKSERVER_PORT:-15071}"
compose=(docker compose --project-name "$project" -f "$repo_root/docker-compose.yml")
finish() {
    status=$?
    trap - EXIT
    if [ "$status" -ne 0 ]; then "${compose[@]}" ps || true; "${compose[@]}" logs --tail 100 || true; fi
    "${compose[@]}" down --volumes --remove-orphans >/dev/null 2>&1 || true
    exit "$status"
}
trap finish EXIT
cd "$repo_root"
export AGENT_STUDIO_VERSION="$previous"
"${compose[@]}" pull
"${compose[@]}" up -d --wait
studio_token="$("${compose[@]}" exec -T task-server cat /run/agent-studio-secrets/studio_token)"
secret_sha="$("${compose[@]}" exec -T task-server sha256sum /run/agent-studio-secrets/studio_token | cut -d' ' -f1)"
workspace="$(curl --fail --silent --show-error -X POST \
    -H "Authorization: Bearer $studio_token" \
    -H 'X-Task-Protocol-Version: 2' -H 'Content-Type: application/json' \
    -d '{"name":"Upgrade smoke"}' \
    "http://127.0.0.1:${STUDIO_TASKSERVER_PORT}/api/v1/workspaces")"
workspace_id="$(jq -r '.workspaceId' <<<"$workspace")"
test -n "$workspace_id" && test "$workspace_id" != null
"${compose[@]}" down
export AGENT_STUDIO_VERSION="$current"
"${compose[@]}" pull
"${compose[@]}" up -d --wait
new_secret_sha="$("${compose[@]}" exec -T task-server sha256sum /run/agent-studio-secrets/studio_token | cut -d' ' -f1)"
test "$secret_sha" = "$new_secret_sha"
workspaces="$(curl --fail --silent --show-error \
    -H "Authorization: Bearer $studio_token" -H 'X-Task-Protocol-Version: 2' \
    "http://127.0.0.1:${STUDIO_TASKSERVER_PORT}/api/v1/workspaces")"
jq -e --arg id "$workspace_id" 'any(.[]; .workspaceId == $id)' <<<"$workspaces" >/dev/null
printf 'compose-upgrade=passed previous=%s current=%s workspace=%s secrets=reused\n' \
    "$previous" "$current" "$workspace_id"
