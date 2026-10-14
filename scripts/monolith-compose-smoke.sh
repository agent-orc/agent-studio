#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
compose_file="$repo_root/deploy/compose/monolith/compose.yaml"
smoke_root="$(mktemp -d)"
export MONOLITH_API_IMAGE="${SMOKE_API_IMAGE:-agent-studio-monolith-smoke:local}"
export MONOLITH_VERSION="$(tr -d '\r\n' < "$repo_root/VERSION")"
export MONOLITH_WORKSPACE_DIR="$smoke_root/workspace"
export MONOLITH_PROJECT_CHECKOUT="$smoke_root/project"
export MONOLITH_PROJECT_NAME="demo"
export MONOLITH_CONFIG_FILE="$smoke_root/appsettings.Production.json"
export MONOLITH_GIT_CREDENTIALS_FILE="$smoke_root/git-credentials"
export MONOLITH_SSH_DIR="$smoke_root/ssh"
export MONOLITH_HOME_DIR="$smoke_root/home"
export MONOLITH_CLAUDE_AUTH_DIR="$smoke_root/claude"
export MONOLITH_CODEX_AUTH_DIR="$smoke_root/codex"
export MONOLITH_GEMINI_AUTH_DIR="$smoke_root/gemini"
export MONOLITH_REMOTE_GATE_SSH_HOST=gate-worker
export MONOLITH_REMOTE_GATE_WORKER_PATH=/opt/agent-studio/gate-worker/OrchestratorApi.dll
export MONOLITH_STUDIO_DIST_DIR="$smoke_root/studio"
readarray -t smoke_ports < <(python3 -c 'import socket; a=socket.socket(); b=socket.socket(); a.bind(("127.0.0.1", 0)); b.bind(("127.0.0.1", 0)); print(a.getsockname()[1]); print(b.getsockname()[1])')
export MONOLITH_API_PORT="${smoke_ports[0]}"
export MONOLITH_STUDIO_PORT="${smoke_ports[1]}"
export COMPOSE_PROJECT_NAME="monolith-smoke-$$"

finish() {
  local result=$?
  if [ "$result" -ne 0 ] && [ -n "${JOB_RESULTS_DIR:-}" ]; then
    mkdir -p "$JOB_RESULTS_DIR"
    docker compose -f "$compose_file" logs --tail 200 > "$JOB_RESULTS_DIR/monolith-smoke.log" 2>&1 || true
  fi
  docker compose -f "$compose_file" down --remove-orphans >/dev/null 2>&1 || true
  if docker image inspect node:22.16.0-alpine >/dev/null 2>&1; then
    docker run --rm --user 0 -v "$smoke_root:/fixtures" --entrypoint chown node:22.16.0-alpine -R "$(id -u):$(id -g)" /fixtures >/dev/null 2>&1 || true
  fi
  rm -rf "$smoke_root" || true
  exit "$result"
}
trap finish EXIT

mkdir -p "$MONOLITH_WORKSPACE_DIR/projects/demo" "$MONOLITH_PROJECT_CHECKOUT" "$MONOLITH_SSH_DIR" "$MONOLITH_HOME_DIR" "$MONOLITH_STUDIO_DIST_DIR" "$MONOLITH_CLAUDE_AUTH_DIR" "$MONOLITH_CODEX_AUTH_DIR" "$MONOLITH_GEMINI_AUTH_DIR"
for auth_dir in "$MONOLITH_CLAUDE_AUTH_DIR" "$MONOLITH_CODEX_AUTH_DIR" "$MONOLITH_GEMINI_AUTH_DIR"; do
  printf 'smoke seed\n' > "$auth_dir/smoke-marker"
done
chmod 755 "$smoke_root" "$MONOLITH_WORKSPACE_DIR" "$MONOLITH_WORKSPACE_DIR/projects" "$MONOLITH_WORKSPACE_DIR/projects/demo" "$MONOLITH_PROJECT_CHECKOUT" "$MONOLITH_HOME_DIR" "$MONOLITH_SSH_DIR" "$MONOLITH_STUDIO_DIST_DIR"
git -C "$MONOLITH_WORKSPACE_DIR" init -q -b main
git -C "$MONOLITH_WORKSPACE_DIR" config user.name 'Monolith Smoke'
git -C "$MONOLITH_WORKSPACE_DIR" config user.email 'monolith-smoke@example.invalid'
touch "$MONOLITH_WORKSPACE_DIR/projects/demo/.keep"
git -C "$MONOLITH_WORKSPACE_DIR" add .
git -C "$MONOLITH_WORKSPACE_DIR" commit -qm 'Initialize synthetic workspace'
git -C "$MONOLITH_PROJECT_CHECKOUT" init -q -b main
git -C "$MONOLITH_PROJECT_CHECKOUT" config user.name 'Monolith Smoke'
git -C "$MONOLITH_PROJECT_CHECKOUT" config user.email 'monolith-smoke@example.invalid'
touch "$MONOLITH_PROJECT_CHECKOUT/.keep"
git -C "$MONOLITH_PROJECT_CHECKOUT" add .
git -C "$MONOLITH_PROJECT_CHECKOUT" commit -qm 'Initialize local project'
cat > "$MONOLITH_CONFIG_FILE" <<'JSON'
{
  "TaskRepository": "/data/workspace",
  "WatchPaths": [{"Name":"Demo", "Path":"/data/workspace/projects/demo", "RootPath":"/data/projects/demo", "RepositoryPath":"/data/projects/demo"}],
  "UpdateService": {"ProbeEnabled": false},
  "WorkspaceEvidence": {"Enabled": true, "DebounceSeconds": 1, "MaxDelaySeconds": 3, "TickSeconds": 1, "Push": false}
}
JSON
printf 'Host *\n  StrictHostKeyChecking yes\n  UserKnownHostsFile /data/home/.ssh/known_hosts\n' > "$MONOLITH_SSH_DIR/config"
touch "$MONOLITH_SSH_DIR/known_hosts" "$MONOLITH_SSH_DIR/id_ed25519" "$MONOLITH_GIT_CREDENTIALS_FILE"
printf '<!doctype html><title>Monolith smoke</title>\n' > "$MONOLITH_STUDIO_DIST_DIR/index.html"
chmod -R a+rwX "$MONOLITH_WORKSPACE_DIR" "$MONOLITH_PROJECT_CHECKOUT" "$MONOLITH_HOME_DIR"
chmod -R a+rX "$MONOLITH_SSH_DIR" "$MONOLITH_STUDIO_DIST_DIR"
chmod 700 "$MONOLITH_SSH_DIR"
chmod 600 "$MONOLITH_SSH_DIR/config" "$MONOLITH_SSH_DIR/id_ed25519"
docker run --rm --user 0 -v "$smoke_root:/fixtures" --entrypoint chown node:22.16.0-alpine -R 10001:10001 /fixtures/ssh /fixtures/claude /fixtures/codex /fixtures/gemini

if [ -z "${SMOKE_API_IMAGE:-}" ]; then
  docker compose -f "$compose_file" build orchestrator-api
fi
docker compose -f "$compose_file" up -d --no-build
docker compose -f "$compose_file" exec -T orchestrator-api sh -c 'test "$(getent passwd 10001 | cut -d: -f6)" = /data/home && ssh -G github.com | grep -q "^userknownhostsfile /data/home/.ssh/known_hosts$" && command -v claude && command -v codex && command -v gemini' >/dev/null
docker compose -f "$compose_file" exec -T orchestrator-api sh -c 'for provider in claude codex gemini; do test -f "/data/home/.$provider/smoke-marker" && test -w "/data/home/.$provider" && ! touch "/run/cli-auth/$provider/smoke-marker"; done' >/dev/null 2>&1
for attempt in $(seq 1 90); do
  if curl -fsS "http://127.0.0.1:$MONOLITH_API_PORT/healthz" >/dev/null && curl -fsS "http://127.0.0.1:$MONOLITH_STUDIO_PORT/" >/dev/null; then break; fi
  if [ "$attempt" -eq 90 ]; then echo 'Monolith did not become healthy' >&2; exit 1; fi
  sleep 2
done
curl -fsS "http://127.0.0.1:$MONOLITH_STUDIO_PORT/api/tasks?project=PROJ-001" >/dev/null
created="$(curl -sS -w '\nHTTP_STATUS:%{http_code}' -H 'X-Client-Id: local-default' -H 'Content-Type: application/json' -d '{"title":"Docker monolith smoke", "project":"PROJ-001", "targetState":"0-backlog", "promptMarkdown":"Verify the Linux monolith."}' "http://127.0.0.1:$MONOLITH_API_PORT/api/tasks")"
if [ "${created##*HTTP_STATUS:}" != 200 ]; then echo "Task create failed: $created" >&2; exit 1; fi
created="${created%$'\n'HTTP_STATUS:*}"
job_id="$(jq -er '.id' <<< "$created")"
curl -fsS -X POST -H 'X-Client-Id: local-default' -H 'Content-Type: application/json' -d '{"targetState":"1-preparation"}' "http://127.0.0.1:$MONOLITH_API_PORT/api/tasks/$job_id/move?project=PROJ-001" >/dev/null
for attempt in $(seq 1 30); do
  if [ -n "$(git -C "$MONOLITH_WORKSPACE_DIR" log -1 --format=%H -- projects/demo/tasks)" ]; then break; fi
  if [ "$attempt" -eq 30 ]; then echo 'No task evidence commit landed in workspace repo' >&2; exit 1; fi
  sleep 2
done
echo "Monolith smoke passed: health, Studio, board, task create/move, workspace commit ($job_id)"
