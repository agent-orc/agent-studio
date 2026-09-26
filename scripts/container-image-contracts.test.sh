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
config_root="$(mktemp -d)"
trap 'rm -rf "$config_root"' EXIT HUP INT TERM
version="$(tr -d '\r\n' < "$repo_root/VERSION")"
compose_json="$(
    AGENT_STUDIO_VERSION="$version" \
    docker compose \
        --project-directory "$config_root" \
        -f "$repo_root/docker-compose.yml" \
        --profile dev \
        config --format json
)"

EXPECTED_VERSION="$version" node -e '
const config = JSON.parse(process.argv[1]);
const services = config.services;
const secretMount = service => services[service]?.volumes?.find(
  volume => volume.source === "secrets" && volume.target === "/run/agent-studio-secrets");
for (const [service, bootstrap] of [["bootstrap", true], ["bootstrap-dev", true],
  ...["task-server", "orchestrator-engine", "studio-bff", "orchestrator-api", "agent-host-distributed"].map(name => [name, false]),
  ...["task-server-dev", "orchestrator-engine-dev", "studio-bff-dev", "orchestrator-api-dev", "agent-host-distributed-dev"].map(name => [name, false])]) {
  const mount = secretMount(service);
  if (!mount || mount.type !== "volume" || Boolean(mount.read_only) === bootstrap)
    throw new Error(`${service} has an invalid secret volume mount`);
}
for (const service of ["task-server", "orchestrator-engine", "studio-bff", "orchestrator-api", "web", "agent-host-distributed"]) {
  if (services[service].build || !services[service].image?.endsWith(`:v${process.env.EXPECTED_VERSION}`))
    throw new Error(`${service} must use the pinned release image`);
}
for (const service of ["task-server-dev", "orchestrator-engine-dev", "studio-bff-dev", "orchestrator-api-dev", "web-dev", "agent-host-distributed-dev"]) {
  if (!services[service].build)
    throw new Error(`${service} must build from this checkout`);
}
for (const [service, dependency] of [["task-server", "bootstrap"], ["task-server-dev", "bootstrap-dev"]]) {
  if (services[service].depends_on?.[dependency]?.condition !== "service_completed_successfully")
    throw new Error(`${service} must wait for credential bootstrap`);
}
for (const service of ["task-server", "task-server-dev", "web", "web-dev"]) {
  if (services[service].ports?.[0]?.host_ip !== "127.0.0.1")
    throw new Error(`${service} must bind to loopback by default`);
}
' "$compose_json"

printf 'Container image user, health, and entrypoint contracts passed.\n'
