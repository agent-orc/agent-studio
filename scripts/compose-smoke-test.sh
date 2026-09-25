#!/usr/bin/env bash
# Acceptance test for the one-box Compose stack. COMPOSE_SMOKE_MODE=dev builds
# this checkout; release pulls the AGENT_STUDIO_VERSION published images.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
repository_version="$(tr -d '\r\n' < "$repo_root/VERSION")"
if [ -n "${AGENT_STUDIO_VERSION:-}" ] && [ "$AGENT_STUDIO_VERSION" != "$repository_version" ]; then
    echo "AGENT_STUDIO_VERSION $AGENT_STUDIO_VERSION does not match VERSION $repository_version" >&2
    exit 64
fi
export AGENT_STUDIO_VERSION="$repository_version"
if [ "${1:-}" = --print-build-version ]; then
    printf '%s\n' "$AGENT_STUDIO_VERSION"
    exit 0
fi
if [ "$#" -ne 0 ]; then
    echo "usage: $0 [--print-build-version]" >&2
    exit 64
fi

mode="${COMPOSE_SMOKE_MODE:-dev}"
project="${COMPOSE_SMOKE_PROJECT:-agent-studio-smoke}"
case "$mode" in
    dev)
        profile=(--profile dev)
        server=task-server-dev
        engine=orchestrator-engine-dev
        bff=studio-bff-dev
        bridge=orchestrator-api-dev
        web=web-dev
        runner=agent-host-dev
        build=(--build)
        ;;
    release)
        # Release images are published as vX.Y.Z, while VERSION is X.Y.Z.
        export AGENT_STUDIO_VERSION="v$repository_version"
        profile=()
        server=task-server
        engine=orchestrator-engine
        bff=studio-bff
        bridge=orchestrator-api
        web=web
        runner=agent-host
        build=()
        ;;
    *) echo "COMPOSE_SMOKE_MODE must be dev or release" >&2; exit 64 ;;
esac
compose=(docker compose --project-name "$project" "${profile[@]}")
fixture="$(mktemp -d)"
override="$fixture/compose.override.yml"
export STUDIO_UI_PORT="${COMPOSE_SMOKE_UI_PORT:-34011}"
export STUDIO_API_PORT="${COMPOSE_SMOKE_API_PORT:-35031}"
export STUDIO_TASKSERVER_PORT="${COMPOSE_SMOKE_TASKSERVER_PORT:-35071}"
export STUDIO_BFF_PORT="${COMPOSE_SMOKE_BFF_PORT:-35072}"

finish() {
    result="$?"
    trap - EXIT
    if [ "$result" -ne 0 ]; then
        "${compose[@]}" ps || true
        "${compose[@]}" logs --tail 100 --no-color || true
    fi
    "${compose[@]}" down --volumes --remove-orphans >/dev/null 2>&1 || true
    docker run --rm -v "$fixture:/fixture" alpine:3.22 chmod -R a+rwx /fixture >/dev/null 2>&1 || true
    rm -rf "$fixture"
    exit "$result"
}
trap finish EXIT
trap 'exit 130' HUP INT TERM

mkdir -p "$fixture/home" "$fixture/runner-work" "$fixture/state"
chmod 755 "$fixture"
chmod 777 "$fixture/home" "$fixture/runner-work" "$fixture/state"
cat > "$fixture/fake-agent.sh" <<'FAKE'
#!/bin/sh
set -eu
if [ "${1:-}" = --version ]; then
    echo 'compose-fake-agent 1.0.0'
    exit 0
fi
mkdir -p "$JOB_RESULTS_DIR"
echo 'compose smoke artifact' > "$JOB_RESULTS_DIR/proof.txt"
echo '{"type":"agent_message","text":"compose smoke run complete"}'
echo '[[TASK_DONE]]'
FAKE
chmod +x "$fixture/fake-agent.sh"

bare="$fixture/origin.git"
seed="$fixture/seed"
git init --quiet --bare "$bare"
git init --quiet -b main "$seed"
echo 'compose fixture' > "$seed/README.md"
git -C "$seed" -c user.name=Smoke -c user.email=smoke@example.invalid add .
git -C "$seed" -c user.name=Smoke -c user.email=smoke@example.invalid commit --quiet -m fixture
git -C "$seed" remote add origin "$bare"
git -C "$seed" push --quiet -u origin main
git -C "$bare" symbolic-ref HEAD refs/heads/main
git -C "$bare" config core.sharedRepository all
chmod -R a+rwX "$bare"

cat > "$override" <<YAML
services:
  $runner:
    volumes:
      - $fixture:/fixtures
      - $fixture/runner-work:/var/lib/agent-host
    environment:
      HOME: /fixtures/home
      RUNNER_WORKDIR: /fixtures/runner-work
      RUNNER_STATE_DIR: /fixtures/state
      RUNNER_GIT_REMOTE: file:///fixtures/origin.git
      RUNNER_GIT_PUSH_REMOTE: file:///fixtures/origin.git
      RUNNER_CLI_BIN: /fixtures/fake-agent.sh
      RUNNER_TTL_SECONDS: "60"
      RUNNER_MAX_PARALLELISM: "1"
      RUNNER_POLL_SECONDS: "1"
YAML
compose+=( -f "$repo_root/docker-compose.yml" -f "$override" )
cd "$repo_root"
"${compose[@]}" config --quiet
"${compose[@]}" down --volumes --remove-orphans >/dev/null 2>&1 || true
"${compose[@]}" up "${build[@]}" --wait bootstrap "$server" "$engine" "$bff" "$bridge" "$web" "$runner"

ui="http://127.0.0.1:$STUDIO_UI_PORT"
tasks="http://127.0.0.1:$STUDIO_TASKSERVER_PORT"
test "$(curl -fsS "$ui/healthz")" = '"ok"'
curl -fsS "$ui/" | grep -q '<app-root'
curl -fsS "$ui/api/v1/protocol" | jq -e . >/dev/null
curl -fsS "http://127.0.0.1:$STUDIO_BFF_PORT/api/v1/protocol" | jq -e . >/dev/null
if [ "${COMPOSE_SMOKE_PLAYWRIGHT:-0}" = 1 ]; then
    (
        cd "$repo_root/frontend"
        PW_BASE_URL="$ui" npx playwright test e2e/docker-compose-routing.spec.ts --project=chromium
    )
fi

token="$("${compose[@]}" exec -T "$server" cat /run/secrets/studio_token)"
for secret in studio_token engine_token runner_token coding_runner_token review_runner_token; do
    test "$("${compose[@]}" exec -T "$server" stat -c %a "/run/secrets/$secret" | tr -d '\r')" = 600
done
request() {
    method="$1" path="$2"
    shift 2
    curl -fsS -X "$method" -H "Authorization: Bearer $token" \
        -H 'X-Task-Protocol-Version: 2' -H 'Content-Type: application/json' \
        "$tasks$path" "$@"
}
workspace="$(request POST /api/v1/workspaces -d '{"name":"Compose smoke"}')"
workspace_id="$(jq -r .workspaceId <<<"$workspace")"
project_json="$(request POST /api/v1/projects -d "$(jq -n --arg ws "$workspace_id" '{workspaceId:$ws,name:"Compose smoke",taskKeyPrefix:"SMK"}')")"
project_id="$(jq -r .projectId <<<"$project_json")"
task="$(request POST "/api/v1/projects/$project_id/tasks" -d '{"title":"Compose smoke task","body":"Prove claim and review.","state":"2-ready"}')"
task_key="$(jq -r .taskKey <<<"$task")"
deadline=$((SECONDS + 180))
state=""
until [ "$state" = 4-auto-review ]; do
    history="$(request GET "/api/v1/projects/$project_id/tasks/$task_key/history")"
    state="$(jq -r .task.state <<<"$history")"
    [ "$state" = 4-auto-review ] && break
    [ "$SECONDS" -lt "$deadline" ] || { echo "task $task_key stayed at $state" >&2; exit 1; }
    sleep 2
done
"${compose[@]}" exec -T "$server" dotnet task-server.dll backup full | jq -e . >/dev/null
engine_secret_before="$("${compose[@]}" exec -T "$server" sha256sum /run/secrets/engine_token | cut -d' ' -f1)"
docker compose --project-name "$project" --profile maintenance \
    -f "$repo_root/docker-compose.yml" -f "$override" run --rm --no-deps \
    -e "ROTATION_TASK_SERVER_URL=http://$server:5071" credential-manager engine
engine_secret_after="$("${compose[@]}" exec -T "$server" sha256sum /run/secrets/engine_token | cut -d' ' -f1)"
test "$engine_secret_before" != "$engine_secret_after"
"${compose[@]}" restart "$engine"
secret_before="$("${compose[@]}" exec -T "$server" sha256sum /run/secrets/studio_token | cut -d' ' -f1)"
"${compose[@]}" down
"${compose[@]}" up --wait bootstrap "$server" "$engine" "$bff" "$bridge" "$web" "$runner"
secret_after="$("${compose[@]}" exec -T "$server" sha256sum /run/secrets/studio_token | cut -d' ' -f1)"
test "$secret_before" = "$secret_after"
request GET "/api/v1/projects/$project_id/tasks/$task_key/history" | jq -e --arg key "$task_key" '.task.taskKey == $key' >/dev/null
if [ "$mode" = release ]; then
    runner_compose=(docker compose --project-name "$project" --profile runner -f "$repo_root/docker-compose.yml" -f "$override")
    "${runner_compose[@]}" up -d --wait agent-host-coding agent-host-review
    runner_token="$("${compose[@]}" exec -T "$server" cat /run/secrets/runner_token)"
    curl -fsS -H "Authorization: Bearer $runner_token" -H 'X-Task-Protocol-Version: 2' \
        "$tasks/api/v1/runners" | jq -e 'map(.runnerId) | index("compose-coding") != null and index("compose-review") != null' >/dev/null
fi
printf 'compose-smoke=passed mode=%s task=%s state=%s persisted=yes\n' "$mode" "$task_key" "$state"
