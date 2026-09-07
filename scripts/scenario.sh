#!/usr/bin/env bash
# One entry point for the deterministic deployment regression scenario.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
target=""
level=""
server_url="${SCENARIO_SERVER_URL:-}"
output="${JOB_RESULTS_DIR:-$repo_root/results}/deployment-scenario"

usage()
{
  printf '%s\n' \
    'Usage: scripts/scenario.sh --target inproc|compose|remote --level smoke|full [--output DIR]' \
    '' \
    'remote requires SCENARIO_SERVER_URL and credentials: SCENARIO_AUTH_TOKEN, or role-specific STUDIO/RUNNER tokens.'
}

while [ "$#" -gt 0 ]; do
  case "$1" in
    --target) target="${2:-}"; shift 2 ;;
    --level) level="${2:-}"; shift 2 ;;
    --output) output="${2:-}"; shift 2 ;;
    --help|-h) usage; exit 0 ;;
    *) printf 'Unknown argument: %s\n' "$1" >&2; usage >&2; exit 2 ;;
  esac
done

case "$target" in inproc|compose|remote) ;; *) usage >&2; exit 2 ;; esac
case "$level" in smoke|full) ;; *) usage >&2; exit 2 ;; esac

mkdir -p "$output"
python_command="python3"
if ! command -v "$python_command" >/dev/null 2>&1; then
  python_command="python"
fi
runner=(
  "$python_command" "$repo_root/testsupport/scenario/run_scenario.py"
  --target "$target"
  --level "$level"
  --manifest "$repo_root/testsupport/scenario/deployment-scenario.json"
  --output "$output"
)

"$python_command" "$repo_root/testsupport/scenario/run_scenario_test.py"

if [ "$target" = inproc ]; then
  dotnet build "$repo_root/task-server/TaskServer.csproj" --configuration Release --nologo
  runner+=(--task-server-dll "$repo_root/task-server/bin/Release/net10.0/task-server.dll")
elif [ "$target" = remote ]; then
  if [ -z "$server_url" ]; then
    printf 'SCENARIO_SERVER_URL is required for --target remote.\n' >&2
    exit 2
  fi
  runner+=(--server-url "$server_url")
else
  command -v docker >/dev/null 2>&1 || { printf 'docker is required for --target compose.\n' >&2; exit 2; }
  project_name="${COMPOSE_SCENARIO_PROJECT:-agent-studio-scenario}"
  ui_port="${COMPOSE_SMOKE_UI_PORT:-4011}"
  api_port="${COMPOSE_SMOKE_API_PORT:-5031}"
  task_server_port="${STUDIO_TASKSERVER_PORT:-5071}"
  export STUDIO_UI_PORT="$ui_port" STUDIO_API_PORT="$api_port" STUDIO_TASKSERVER_PORT="$task_server_port"
  export SCENARIO_AUTH_TOKEN="${SCENARIO_AUTH_TOKEN:-deployment-scenario-token-2739-fixed}"
  SCENARIO_OUTPUT_DIR="$(cd "$(dirname "$output")" && pwd)/$(basename "$output")"
  export SCENARIO_OUTPUT_DIR
  compose=(docker compose --project-name "$project_name" -f "$repo_root/docker-compose.yml" -f "$repo_root/testsupport/scenario/compose.override.yml")

  compose_down()
  {
    "${compose[@]}" --profile distributed down --volumes --remove-orphans >/dev/null 2>&1 || true
  }
  compose_finish()
  {
    status="$?"
    trap - EXIT HUP INT TERM
    if [ "$status" -ne 0 ]; then
      "${compose[@]}" --profile distributed ps || true
      "${compose[@]}" --profile distributed logs --no-color || true
    fi
    compose_down
    exit "$status"
  }
  trap compose_finish EXIT
  trap 'exit 130' HUP INT TERM
  compose_down
  "${compose[@]}" --profile distributed config --quiet
  "${compose[@]}" --profile distributed build scenario-fake-runner
  "${compose[@]}" --profile distributed up --build --wait orchestrator-api frontend task-server studio-bff orchestrator-engine

  health="$(curl --fail --silent "http://127.0.0.1:${ui_port}/healthz")"
  test "$health" = '"ok"'
  curl --fail --silent "http://127.0.0.1:${ui_port}/" | grep -q '<app-root'
  curl --fail --silent "http://127.0.0.1:${ui_port}/api/tasks/grouped" | grep -q '"backlog"'
  "${compose[@]}" --profile distributed ps --status running --services | grep -qx 'orchestrator-engine'
  "${compose[@]}" --profile distributed run --rm scenario-fake-runner \
    --target compose \
    --level "$level" \
    --manifest /scenario/deployment-scenario.json \
    --output /scenario-output \
    --server-url http://task-server:5071
  exit 0
fi

"${runner[@]}"
