#!/usr/bin/env bash
# One-box deployment smoke. Default builds this checkout; set
# COMPOSE_SMOKE_MODE=images to exercise published release images.
set -euo pipefail
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
repository_version="$(tr -d '\r\n' < "$repo_root/VERSION")"
if [ -n "${AGENT_STUDIO_VERSION:-}" ] && [ "$AGENT_STUDIO_VERSION" != "$repository_version" ]; then
    echo "AGENT_STUDIO_VERSION $AGENT_STUDIO_VERSION does not match VERSION $repository_version" >&2
    exit 64
fi
export AGENT_STUDIO_VERSION="$repository_version"
if [ "${1:-}" = "--print-build-version" ]; then
    printf '%s\n' "$AGENT_STUDIO_VERSION"
    exit 0
fi
test "$#" -eq 0 || { echo "usage: $0 [--print-build-version]" >&2; exit 64; }
mode="${COMPOSE_SMOKE_MODE:-dev}"
test "$mode" = dev || test "$mode" = images || { echo "invalid COMPOSE_SMOKE_MODE" >&2; exit 64; }
project="${COMPOSE_SMOKE_PROJECT:-agent-studio-smoke-$$}"
export STUDIO_UI_PORT="${COMPOSE_SMOKE_UI_PORT:-14011}"
export STUDIO_TASKSERVER_PORT="${COMPOSE_SMOKE_TASKSERVER_PORT:-15071}"
fixture="$(mktemp -d)"
override="$fixture/override.yaml"
compose=(docker compose --project-name "$project" -f "$repo_root/docker-compose.yml" -f "$override")
if [ "$mode" = dev ]; then
    suffix=-dev
    services=(task-server-dev orchestrator-engine-dev studio-bff-dev orchestrator-api-dev web-dev agent-host-distributed-dev)
    profiles=(--profile dev)
    build=(--build)
else
    suffix=""
    services=(task-server orchestrator-engine studio-bff orchestrator-api web agent-host-distributed)
    profiles=()
    build=()
fi
finish() {
    status=$?
    trap - EXIT HUP INT TERM
    if [ "$status" -ne 0 ]; then
        "${compose[@]}" "${profiles[@]}" ps || true
        "${compose[@]}" "${profiles[@]}" logs --no-color || true
        if [ "${COMPOSE_SMOKE_KEEP_ON_FAIL:-0}" = 1 ]; then
            printf 'compose-smoke-debug-project=%s fixture=%s\n' "$project" "$fixture" >&2
            exit "$status"
        fi
    fi
    image_id="$("${compose[@]}" "${profiles[@]}" images -q "task-server${suffix}" 2>/dev/null | head -n1)"
    if [ -n "$image_id" ]; then
        docker run --rm --user 0 -v "$fixture:/fixtures" --entrypoint chown \
            "$image_id" -R "$(id -u):$(id -g)" /fixtures >/dev/null 2>&1 || true
    fi
    "${compose[@]}" "${profiles[@]}" down --volumes --remove-orphans >/dev/null 2>&1 || true
    rm -rf "$fixture"
    exit "$status"
}
trap finish EXIT
trap 'exit 130' HUP INT TERM
wait_for_http() {
    local url="$1" deadline=$((SECONDS + 90))
    until curl --fail --silent --output /dev/null "$url"; do
        [ "$SECONDS" -lt "$deadline" ] || { echo "timeout: $url" >&2; return 1; }
        sleep 2
    done
}
call() {
    local method="$1" path="$2"
    shift 2
    curl --fail --silent --show-error -X "$method" \
        -H "Authorization: Bearer $studio_token" \
        -H 'X-Task-Protocol-Version: 2' \
        -H 'Content-Type: application/json' \
        "http://127.0.0.1:${STUDIO_UI_PORT}${path}" "$@"
}
cd "$repo_root"
mkdir -p "$fixture/home" "$fixture/work" "$fixture/state"
git init --quiet --bare "$fixture/origin.git"
git init --quiet -b main "$fixture/seed"
printf 'compose smoke fixture\n' > "$fixture/seed/README.md"
git -C "$fixture/seed" -c user.name='Compose Smoke' -c user.email='smoke@example.invalid' add .
git -C "$fixture/seed" -c user.name='Compose Smoke' -c user.email='smoke@example.invalid' commit --quiet -m fixture
git -C "$fixture/seed" remote add origin "$fixture/origin.git"
git -C "$fixture/seed" push --quiet -u origin main
git -C "$fixture/origin.git" symbolic-ref HEAD refs/heads/main
cat > "$fixture/fake-cli.sh" <<'FAKE'
#!/bin/sh
set -eu
if [ "${1:-}" = '--version' ]; then printf 'fake-cli 1.0\n'; exit 0; fi
mkdir -p "$JOB_RESULTS_DIR"
printf 'compose smoke artifact\n' > "$JOB_RESULTS_DIR/proof.txt"
printf '{"type":"agent_message","text":"compose smoke complete"}\n'
printf '[[TASK_DONE]]\n'
FAKE
chmod +x "$fixture/fake-cli.sh"
chmod -R a+rwX "$fixture"
cat > "$override" <<OVERRIDE
services:
  agent-host-distributed${suffix}:
    volumes:
      - $fixture:/fixtures
      - secrets:/run/agent-studio-secrets:ro
    environment:
      RUNNER_SERVER_URL: http://task-server${suffix}:5071
      RUNNER_ID: distributed-runner
      RUNNER_NAME: distributed-runner
      RUNNER_ALLOW_INSECURE_HTTP: "1"
      RUNNER_AUTH_TOKEN_FILE: /run/agent-studio-secrets/runner_token
      RUNNER_WORKDIR: /fixtures/work
      RUNNER_STATE_DIR: /fixtures/state
      RUNNER_GIT_REMOTE: file:///fixtures/origin.git
      RUNNER_GIT_PUSH_REMOTE: file:///fixtures/origin.git
      RUNNER_CLI_BIN: /fixtures/fake-cli.sh
      RUNNER_TTL_SECONDS: "60"
      RUNNER_MAX_PARALLELISM: "1"
      RUNNER_POLL_SECONDS: "1"
OVERRIDE
# The override is only for the disposable fake CLI. The normal compose file
# remains the deployment contract.
"${compose[@]}" "${profiles[@]}" config --quiet
"${compose[@]}" "${profiles[@]}" up "${build[@]}" --wait "${services[@]}"
wait_for_http "http://127.0.0.1:${STUDIO_UI_PORT}/healthz"
grep -q '<app-root' < <(curl --fail --silent "http://127.0.0.1:${STUDIO_UI_PORT}/")
studio_token="$("${compose[@]}" exec -T "task-server${suffix}" cat /run/agent-studio-secrets/studio_token)"
secret_sha="$("${compose[@]}" exec -T "task-server${suffix}" sha256sum /run/agent-studio-secrets/studio_token | cut -d' ' -f1)"
workspace="$(call POST /api/v1/workspaces -d '{"name":"Compose smoke"}')"
workspace_id="$(jq -r '.workspaceId' <<<"$workspace")"
project_json="$(call POST /api/v1/projects -d "$(jq -n --arg ws "$workspace_id" '{workspaceId:$ws,name:"Agent Studio",taskKeyPrefix:"SMK"}')")"
project_id="$(jq -r '.projectId' <<<"$project_json")"
task="$(call POST "/api/v1/projects/$project_id/tasks" -d '{"title":"Compose smoke task","body":"Prove claim and review.","state":"2-ready"}')"
task_key="$(jq -r '.taskKey' <<<"$task")"
deadline=$((SECONDS + 120))
state=""
until [ "$state" = '4-auto-review' ]; do
    history="$(call GET "/api/v1/projects/$project_id/tasks/$task_key/history")"
    state="$(jq -r '.task.state' <<<"$history")"
    [ "$SECONDS" -lt "$deadline" ] || { echo "task $task_key stalled at $state" >&2; exit 1; }
    sleep 2
done
"${compose[@]}" exec -T "task-server${suffix}" dotnet task-server.dll backup full --json > "$fixture/backup.json"
jq -e '.id // .backupId' "$fixture/backup.json" >/dev/null
"${compose[@]}" "${profiles[@]}" down
"${compose[@]}" "${profiles[@]}" up "${build[@]}" --wait "${services[@]}"
new_secret_sha="$("${compose[@]}" exec -T "task-server${suffix}" sha256sum /run/agent-studio-secrets/studio_token | cut -d' ' -f1)"
test "$new_secret_sha" = "$secret_sha"
history="$(call GET "/api/v1/projects/$project_id/tasks/$task_key/history")"
test "$(jq -r '.task.taskKey' <<<"$history")" = "$task_key"
printf 'compose-smoke=passed mode=%s task=%s state=%s backup=created secrets=reused data=retained\n' "$mode" "$task_key" "$state"
