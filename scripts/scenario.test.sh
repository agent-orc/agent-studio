#!/usr/bin/env bash
# Focused shell contract tests for Compose startup behavior in scenario.sh.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
test_root="$(mktemp -d "${TMPDIR:-/tmp}/agent-studio-scenario-contract.XXXXXX")"
trap 'rm -rf -- "$test_root"' EXIT

fake_bin="$test_root/bin"
fake_state="$test_root/state"
mkdir -p "$fake_bin" "$fake_state"

cat >"$fake_bin/dotnet" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
if [ "${1:-}" = "test" ]; then
    printf '%s\n' "${SCENARIO_TARGET_URL:-}" >"$FAKE_DOCKER_STATE/target-url"
    printf '%s\n' "${SCENARIO_STUDIO_BFF_URL:-}" >"$FAKE_DOCKER_STATE/bff-url"
    printf '%s:%s\n' "${SCENARIO_UID:-}" "${SCENARIO_GID:-}" >"$FAKE_DOCKER_STATE/uid-gid"
    printf '%s\n' "${SCENARIO_HOST_DIR:-}" >"$FAKE_DOCKER_STATE/host-dir"
    mkdir -p "$SCENARIO_HOST_DIR/runner-work"
    printf 'fixture output\n' >"$SCENARIO_HOST_DIR/runner-work/owned-by-scenario-user"
fi
EOF

cat >"$fake_bin/docker" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >>"$FAKE_DOCKER_STATE/calls"

if [ "${1:-}" = "inspect" ]; then
    printf '%s\n' "${FAKE_CONTAINER_STATE:-running}"
    exit 0
fi

[ "${1:-}" = "compose" ] || exit 2
shift
while [ "$#" -gt 0 ]; do
    case "$1" in
        --project-name|--file|--profile)
            shift 2
            ;;
        *)
            break
            ;;
    esac
done

command="${1:-}"
[ "$#" -eq 0 ] || shift
case "$command" in
    config|build|up|down)
        exit 0
        ;;
    images)
        printf 'sha256:scenario-task-server\n'
        ;;
    port)
        service="${1:-}"
        count_file="$FAKE_DOCKER_STATE/port-$service"
        count=0
        [ ! -f "$count_file" ] || count="$(cat "$count_file")"
        count=$((count + 1))
        printf '%s\n' "$count" >"$count_file"
        if [ "${FAKE_DOCKER_MODE:-retry}" = "failure" ] \
            || { [ "$service" = "task-server" ] && [ "$count" -eq 1 ]; }; then
            printf 'no port for %s\n' "$service" >&2
            exit 1
        fi
        case "$service" in
            task-server) printf '127.0.0.1:41071\n' ;;
            studio-bff) printf '127.0.0.1:41072\n' ;;
            *) exit 2 ;;
        esac
        ;;
    ps)
        case " $* " in
            *" --quiet "*) printf 'scenario-container-id\n' ;;
            *) printf 'NAME STATUS\nscenario-task-server running\n' ;;
        esac
        ;;
    logs)
        printf 'scenario fake Compose logs\n'
        ;;
    *)
        printf 'unexpected fake docker command: %s\n' "$command" >&2
        exit 2
        ;;
esac
EOF
chmod 0755 "$fake_bin/dotnet" "$fake_bin/docker"

# Keep image entrypoints aligned with the AssemblyName values produced by
# dotnet publish. This catches the startup failure without requiring Docker.
grep -F 'ENTRYPOINT ["dotnet", "task-server.dll"]' "$repo_root/task-server/Dockerfile"
grep -F 'ENTRYPOINT ["dotnet", "agent-studio-bff.dll"]' "$repo_root/studio-bff/Dockerfile"
grep -F 'ENV URLS=http://0.0.0.0:5072 \' "$repo_root/studio-bff/Dockerfile"
if grep -F 'ENV ASPNETCORE_URLS=' "$repo_root/studio-bff/Dockerfile"; then
    printf 'Studio BFF Dockerfile uses ASPNETCORE_URLS, which appsettings Urls overrides.\n' >&2
    exit 1
fi
grep -F 'ENTRYPOINT ["dotnet", "orchestrator-engine.dll"]' "$repo_root/orchestrator-engine/Dockerfile"
grep -F 'ENTRYPOINT ["dotnet", "/opt/agent-host/agent-host.dll", "--poll"]' \
    "$repo_root/runner/Dockerfile" "$repo_root/testsupport/scenario/runner.Dockerfile"
grep -F 'user: "${SCENARIO_UID:?set SCENARIO_UID}:${SCENARIO_GID:?set SCENARIO_GID}"' \
    "$repo_root/testsupport/scenario/docker-compose.scenario.yml"
grep -F '"--user", $"{RequiredEnvironment("SCENARIO_UID")}:{RequiredEnvironment("SCENARIO_GID")}",' \
    "$repo_root/task-server.Tests/ScenarioContext.cs"

render_host="$test_root/render-host"
mkdir -p "$render_host"
scenario_version="$(tr -d '\r\n' < "$repo_root/VERSION")"
scenario_compose_json="$(
    SCENARIO_UID="$(id -u)" \
    SCENARIO_GID="$(id -g)" \
    SCENARIO_HOST_DIR="$render_host" \
    SCENARIO_BUILD_VERSION="$scenario_version" \
    SCENARIO_BUILD_SHA=scenario-contract \
    SCENARIO_TASK_SERVER_IMAGE=scenario-contract-task-server:local \
    SCENARIO_STUDIO_BFF_IMAGE=scenario-contract-studio-bff:local \
    SCENARIO_AGENT_HOST_IMAGE=scenario-contract-agent-host:local \
    DISTRIBUTED_STUDIO_TOKEN=scenario-contract-studio-token \
    DISTRIBUTED_ENGINE_TOKEN=scenario-contract-engine-token \
    DISTRIBUTED_RUNNER_TOKEN=scenario-contract-runner-token \
    docker compose \
        --project-name scenario-contract-rendered \
        --file "$repo_root/docker-compose.yml" \
        --file "$repo_root/testsupport/scenario/docker-compose.scenario.yml" \
        --profile distributed \
        --profile runner \
        config --format json
)"

node -e '
const config = JSON.parse(process.argv[1]);
const expected = {
  "task-server": {
    image: "scenario-contract-task-server:local",
    dockerfile: "task-server/Dockerfile",
  },
  "studio-bff": {
    image: "scenario-contract-studio-bff:local",
    dockerfile: "studio-bff/Dockerfile",
  },
  "agent-host-distributed": {
    image: "scenario-contract-agent-host:local",
    dockerfile: "testsupport/scenario/runner.Dockerfile",
  },
};
for (const [serviceName, contract] of Object.entries(expected)) {
  const service = config.services[serviceName];
  if (service?.image !== contract.image) {
    throw new Error(`${serviceName} image is ${service?.image}, expected ${contract.image}`);
  }
  if (service?.build?.dockerfile !== contract.dockerfile) {
    throw new Error(`${serviceName} does not build ${contract.dockerfile}`);
  }
}
for (const source of ["studio_token", "engine_token", "runner_token"]) {
  const secret = config.services["task-server"].secrets
    .find(candidate => candidate.source === source);
  if (!secret || String(secret.uid) !== "10001" || String(secret.gid) !== "10001") {
    throw new Error(`task-server/${source} is not readable by UID/GID 10001`);
  }
  if (String(secret.mode) !== "0400") {
    throw new Error(`task-server/${source} mode is not 0400`);
  }
}
if (config.services["task-server"].build.args.VERSION !== process.argv[2]
    || config.services["studio-bff"].build.args.VERSION !== process.argv[2]) {
  throw new Error("scenario service builds do not use the canonical repository version");
}
' "$scenario_compose_json" "$scenario_version"

success_report="$test_root/success-report"
PATH="$fake_bin:$PATH" \
FAKE_DOCKER_STATE="$fake_state" \
FAKE_DOCKER_MODE=retry \
COMPOSE_SCENARIO_PROJECT=scenario-contract-success \
SCENARIO_COMPOSE_PORT_TIMEOUT_SECONDS=3 \
    "$repo_root/scripts/scenario.sh" \
    --target compose --level full --report-dir "$success_report"

[ "$(cat "$fake_state/port-task-server")" -eq 2 ]
[ "$(cat "$fake_state/target-url")" = "http://127.0.0.1:41071" ]
[ "$(cat "$fake_state/bff-url")" = "http://127.0.0.1:41072" ]
[ "$(cat "$fake_state/uid-gid")" = "$(id -u):$(id -g)" ]
[ ! -e "$(cat "$fake_state/host-dir")" ]
[ ! -e "$success_report/scenario-compose-full.compose.log" ]

rm -f -- "$fake_state/port-task-server" "$fake_state/port-studio-bff"
failure_report="$test_root/failure-report"
if PATH="$fake_bin:$PATH" \
    FAKE_DOCKER_STATE="$fake_state" \
    FAKE_DOCKER_MODE=failure \
    COMPOSE_SCENARIO_PROJECT=scenario-contract-failure \
    SCENARIO_COMPOSE_PORT_TIMEOUT_SECONDS=1 \
        "$repo_root/scripts/scenario.sh" \
        --target compose --level full --report-dir "$failure_report"
then
    printf 'Compose startup unexpectedly succeeded without a published port.\n' >&2
    exit 1
fi

grep -F 'scenario fake Compose logs' "$failure_report/scenario-compose-full.compose.log"
grep -F 'failures="1"' "$failure_report/scenario-compose-full.junit.xml"
grep -F 'scenario-compose-full.compose.log' "$failure_report/scenario-compose-full.md"
grep -F 'compose --project-name scenario-contract-failure' "$fake_state/calls"

printf 'Scenario Compose startup contract tests passed.\n'
