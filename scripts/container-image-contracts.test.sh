#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dockerfiles=(
    backend/Dockerfile
    frontend/Dockerfile
    task-server/Dockerfile
    orchestrator-engine/Dockerfile
    studio-bff/Dockerfile
    runner/Dockerfile
)

for relative in "${dockerfiles[@]}"; do
    dockerfile="$repo_root/$relative"
    runtime_user="$(sed -n 's/^USER[[:space:]]\+//p' "$dockerfile" | tail -n 1)"
    if [ -z "$runtime_user" ] || [ "$runtime_user" = root ] || [ "$runtime_user" = "0" ]; then
        echo "$relative does not declare a non-root runtime user" >&2
        exit 1
    fi
    grep -q '^HEALTHCHECK ' "$dockerfile"
done

grep -F 'ARG CODEX_CLI_VERSION=0.154.0' "$repo_root/runner/Dockerfile" > /dev/null
grep -F 'ARG CLAUDE_CLI_VERSION=2.1.269' "$repo_root/runner/Dockerfile" > /dev/null
grep -F '"@openai/codex@${CODEX_CLI_VERSION}"' "$repo_root/runner/Dockerfile" > /dev/null
grep -F '"@anthropic-ai/claude-code@${CLAUDE_CLI_VERSION}"' "$repo_root/runner/Dockerfile" > /dev/null

# .NET 10 base images already reserve the app account. Creating it again makes
# an otherwise valid image fail during its runtime stage.
for relative in \
    backend/Dockerfile \
    task-server/Dockerfile \
    orchestrator-engine/Dockerfile \
    studio-bff/Dockerfile
do
    if grep -Eq '(groupadd|useradd).* app([[:space:]]|$)' "$repo_root/$relative"; then
        echo "$relative attempts to recreate the base image app account" >&2
        exit 1
    fi
done

# Resolve assembly names from the projects themselves, then require each
# Docker entrypoint to name the published DLL. This keeps image startup aligned
# when a project uses AssemblyName rather than its csproj filename.
while IFS='|' read -r relative project
do
    assembly="$(dotnet msbuild "$repo_root/$project" -getProperty:AssemblyName -nologo)"
    grep -F "${assembly}.dll" "$repo_root/$relative" > /dev/null || {
        echo "$relative does not launch published assembly ${assembly}.dll" >&2
        exit 1
    }
done <<'EOF'
backend/Dockerfile|backend/OrchestratorApi.csproj
task-server/Dockerfile|task-server/TaskServer.csproj
orchestrator-engine/Dockerfile|orchestrator-engine/OrchestratorEngine.csproj
studio-bff/Dockerfile|studio-bff/StudioBff.csproj
runner/Dockerfile|runner/AgentRunner.csproj
EOF

grep -F 'FROM mcr.microsoft.com/dotnet/aspnet:10.0' \
    "$repo_root/orchestrator-engine/Dockerfile" > /dev/null
grep -F 'ENV URLS=http://0.0.0.0:5072' \
    "$repo_root/studio-bff/Dockerfile" > /dev/null
grep -F 'user: "$smoke_uid:$smoke_gid"' \
    "$repo_root/scripts/compose-smoke-test.sh" > /dev/null
grep -F 'RUNNER_WORKDIR: /fixtures/runner-work' \
    "$repo_root/scripts/compose-smoke-test.sh" > /dev/null
grep -F 'uid: "$smoke_uid"' \
    "$repo_root/scripts/compose-smoke-test.sh" > /dev/null
grep -F 'mode: 0400' \
    "$repo_root/scripts/compose-smoke-test.sh" > /dev/null
if grep -F 'chmod -R o+rwX "$fixture_dir"' \
    "$repo_root/scripts/compose-smoke-test.sh" > /dev/null; then
    echo "compose smoke globally weakens disposable fixture permissions" >&2
    exit 1
fi

config_root="$(mktemp -d)"
trap 'rm -rf "$config_root"' EXIT HUP INT TERM
: > "$config_root/runner.env"
version="$(tr -d '\r\n' < "$repo_root/VERSION")"
compose_json="$(
    AGENT_STUDIO_VERSION="$version" \
    DISTRIBUTED_ENGINE_TOKEN=container-image-contract-test \
    docker compose \
        --project-directory "$config_root" \
        -f "$repo_root/docker-compose.yml" \
        --profile dev \
        --profile distributed \
        config --format json
)"

node -e '
const config = JSON.parse(process.argv[1]);
const expected = {
  "task-server": ["studio_token", "engine_token", "runner_token"],
  "task-server-dev": ["studio_token", "engine_token", "runner_token"],
  "studio-bff": ["studio_token"],
  "studio-bff-dev": ["studio_token"],
  "agent-host-distributed": ["runner_token"],
  "agent-host-distributed-dev": ["runner_token"],
};
for (const serviceName of ["orchestrator-engine", "orchestrator-engine-dev"]) {
  const environment = config.services[serviceName]?.environment ?? {};
  if (environment.ENGINE_ALLOW_INSECURE_HTTP !== "1") {
    throw new Error(`${serviceName} does not explicitly opt in to private-network HTTP`);
  }
}
const healthyDependencies = {
  "orchestrator-engine": "task-server",
  "orchestrator-engine-dev": "task-server-dev",
};
for (const [serviceName, dependencyName] of Object.entries(healthyDependencies)) {
  const condition = config.services[serviceName]?.depends_on?.[dependencyName]?.condition;
  if (condition !== "service_healthy") {
    throw new Error(`${serviceName} starts before ${dependencyName} is healthy`);
  }
}
for (const [serviceName, sources] of Object.entries(expected)) {
  const mounted = config.services[serviceName]?.secrets ?? [];
  for (const source of sources) {
    const secret = mounted.find(candidate => candidate.source === source);
    if (!secret) throw new Error(`${serviceName} does not mount ${source}`);
    if (String(secret.uid) !== "10001" || String(secret.gid) !== "10001") {
      throw new Error(`${serviceName}/${source} is not owned by UID/GID 10001`);
    }
    if (String(secret.mode) !== "0400") {
      throw new Error(`${serviceName}/${source} mode is ${secret.mode}, expected 0400`);
    }
  }
}
' "$compose_json"

printf 'Container image user, health, and entrypoint contracts passed.\n'
