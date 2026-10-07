#!/usr/bin/env bash
# Focused shell contract tests for Compose startup behavior in scenario.sh and
# for the Docker images it leaves behind (AGT-2993).
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
    if [ -n "${FAKE_DOTNET_TEST_BLOCK:-}" ]; then
        # Stand-in for a long scenario run that the caller terminates.
        : >"$FAKE_DOTNET_TEST_BLOCK"
        sleep 60
    fi
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

# Image, container and builder commands go to the stateful image store, so the
# test can see which images a run left behind.
case "${1:-}" in
    image|ps|container|builder) exec "$FAKE_DOCKER_IMAGE_STORE" "$@" ;;
esac

[ "${1:-}" = "compose" ] || exit 2
shift
project=""
while [ "$#" -gt 0 ]; do
    case "$1" in
        --project-name)
            project="$2"
            shift 2
            ;;
        --file|--profile)
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
    build)
        for ref in "$SCENARIO_TASK_SERVER_IMAGE" "$SCENARIO_STUDIO_BFF_IMAGE" \
            "$SCENARIO_ORCHESTRATOR_ENGINE_IMAGE" "$SCENARIO_AGENT_HOST_IMAGE"; do
            "$FAKE_DOCKER_IMAGE_STORE" _add "$ref" "sha256:$ref" \
                "com.docker.compose.project=$project,io.agent-studio.disposable-image=scenario" \
                "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
        done
        ;;
    config|up|down)
        exit 0
        ;;
    exec)
        case "$*" in
            *studio_token) printf 'scenario-contract-studio-token\n' ;;
            *engine_token) printf 'scenario-contract-engine-token\n' ;;
            *runner_token) printf 'scenario-contract-runner-token\n' ;;
            *) exit 2 ;;
        esac
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
    SCENARIO_ORCHESTRATOR_ENGINE_IMAGE=scenario-contract-engine:local \
    SCENARIO_AGENT_HOST_IMAGE=scenario-contract-agent-host:local \
    docker compose \
        --project-name scenario-contract-rendered \
        --file "$repo_root/docker-compose.yml" \
        --file "$repo_root/testsupport/scenario/docker-compose.scenario.yml" \
        config --format json
)"

# The one-box Compose file has no distributed or runner profile; the scenario
# must neither hide its runner behind one nor select one that does not exist.
if grep -En -- '--profile.{0,4}(distributed|runner)' \
    "$repo_root/scripts/scenario.sh" "$repo_root/task-server.Tests/ScenarioContext.cs"; then
    printf 'The scenario selects a Compose profile the one-box file does not define.\n' >&2
    exit 1
fi

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
  "orchestrator-engine": {
    image: "scenario-contract-engine:local",
    dockerfile: "orchestrator-engine/Dockerfile",
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
  // AGT-2993: the retention helper finds scenario images by this label under
  // any project name.
  if (service.build.labels?.["io.agent-studio.disposable-image"] !== "scenario") {
    throw new Error(`${serviceName} image is not labelled io.agent-studio.disposable-image=scenario`);
  }
}
const tokenFiles = {
  "task-server": {
    STUDIO_AUTH_TOKEN_FILE: "studio/studio_token",
    ENGINE_AUTH_TOKEN_FILE: "engine/engine_token",
    BOOTSTRAP_RUNNER_AUTH_TOKEN_FILE: "runner/runner_token",
  },
  "studio-bff": { TaskServer__AuthTokenFile: "studio_token" },
  "orchestrator-engine": { CLIENT_CREDENTIAL_FILE: "engine_token" },
};
for (const [serviceName, files] of Object.entries(tokenFiles)) {
  const service = config.services[serviceName];
  const volume = service.volumes.find(volume => volume.target === "/run/agent-studio-secrets");
  if (volume?.source !== "secrets" || !volume.read_only) {
    throw new Error(`${serviceName} must read the Compose credential volume`);
  }
  if (serviceName === "studio-bff" && volume.volume?.subpath !== "studio" ||
      serviceName === "orchestrator-engine" && volume.volume?.subpath !== "engine") {
    throw new Error(`${serviceName} can read another service credential`);
  }
  for (const [variable, file] of Object.entries(files)) {
    if (service.environment?.[variable] !== `/run/agent-studio-secrets/${file}`) {
      throw new Error(`${serviceName}/${variable} must read its bootstrapped credential`);
    }
  }
}
// Keep the upstream source-image boundary, including the one-shot bootstrap.
for (const serviceName of ["bootstrap", "task-server", "studio-bff", "orchestrator-engine", "agent-host-distributed"]) {
  const image = config.services[serviceName]?.image ?? "";
  if (image === "" || image.startsWith("ghcr.io/")) {
    throw new Error(`${serviceName} would run the published image ${image} in the scenario`);
  }
}
if (config.services["bootstrap"].image !== config.services["task-server"].image) {
  throw new Error("bootstrap does not run from the scenario task-server image");
}
// The harness reaches the Task Server and the Studio BFF directly on the
// loopback interface; the product stack publishes only the Task Server port.
for (const [serviceName, containerPort] of [["task-server", 5071], ["studio-bff", 5072]]) {
  const published = (config.services[serviceName].ports ?? [])
    .find(port => Number(port.target) === containerPort && port.host_ip === "127.0.0.1");
  if (!published) {
    throw new Error(`${serviceName} does not publish port ${containerPort} on 127.0.0.1 for the scenario harness`);
  }
}
if (config.services["task-server"].build.args.VERSION !== process.argv[2]
    || config.services["studio-bff"].build.args.VERSION !== process.argv[2]) {
  throw new Error("scenario service builds do not use the canonical repository version");
}
' "$scenario_compose_json" "$scenario_version"

export FAKE_DOCKER_IMAGE_STORE="$repo_root/scripts/fixtures/fake-docker-image-store.sh"
scenario_images() { cut -f1 "$fake_state/images" | grep -F -- "$1-" || true; }

# Residue of an older, killed run: the retention pass at the start of the next
# run removes it; a published image stays.
FAKE_DOCKER_STATE="$fake_state" "$FAKE_DOCKER_IMAGE_STORE" _add \
    agent-studio-scenario-999-task-server:local sha256:killed-run \
    io.agent-studio.disposable-image=scenario 2026-01-01T00:00:00Z
FAKE_DOCKER_STATE="$fake_state" "$FAKE_DOCKER_IMAGE_STORE" _add \
    ghcr.io/agent-orc/agent-task-server:v0.9.1 sha256:published "" 2026-01-01T00:00:00Z

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
grep -F 'compose --project-name scenario-contract-success' "$fake_state/calls" | grep -F ' build ' >/dev/null
[ -z "$(scenario_images scenario-contract-success)" ] \
    || { printf 'Images left after a passed run:\n%s\n' "$(scenario_images scenario-contract-success)" >&2; exit 1; }
[ -z "$(scenario_images agent-studio-scenario-999)" ] \
    || { printf 'Retention did not clear the killed run residue.\n' >&2; exit 1; }
grep -Fx 'builder prune --force --max-used-space 40GB' "$fake_state/calls" >/dev/null
grep -F 'ghcr.io/agent-orc/agent-task-server:v0.9.1' "$fake_state/images" >/dev/null

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
[ -z "$(scenario_images scenario-contract-failure)" ] \
    || { printf 'Images left after a failed run:\n%s\n' "$(scenario_images scenario-contract-failure)" >&2; exit 1; }

# SIGTERM while the scenario runs: the trap still tears the stack down and
# removes the run's images. setsid gives the run its own process group, the way
# a supervisor stops the whole tree.
rm -f -- "$fake_state/port-task-server" "$fake_state/port-studio-bff"
term_report="$test_root/term-report"
term_marker="$test_root/dotnet-test-started"
PATH="$fake_bin:$PATH" \
FAKE_DOCKER_STATE="$fake_state" \
FAKE_DOCKER_MODE=retry \
FAKE_DOTNET_TEST_BLOCK="$term_marker" \
COMPOSE_SCENARIO_PROJECT=scenario-contract-term \
SCENARIO_COMPOSE_PORT_TIMEOUT_SECONDS=3 \
    setsid "$repo_root/scripts/scenario.sh" \
    --target compose --level full --report-dir "$term_report" &
term_pid=$!
for _ in $(seq 1 100); do
    [ ! -e "$term_marker" ] || break
    sleep 0.1
done
[ -e "$term_marker" ] || { printf 'The terminated run never reached dotnet test.\n' >&2; exit 1; }
[ -n "$(scenario_images scenario-contract-term)" ]
kill -TERM -- "-$term_pid"
term_status=0
wait "$term_pid" || term_status=$?
[ "$term_status" -eq 130 ] || { printf 'SIGTERM run exited %s, expected 130.\n' "$term_status" >&2; exit 1; }
[ -z "$(scenario_images scenario-contract-term)" ] \
    || { printf 'Images left after SIGTERM:\n%s\n' "$(scenario_images scenario-contract-term)" >&2; exit 1; }
grep -F 'compose --project-name scenario-contract-term' "$fake_state/calls" | grep -F ' down ' >/dev/null

printf 'Scenario Compose startup contract tests passed.\n'
