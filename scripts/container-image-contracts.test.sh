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

# The default must be the distributed product, with bootstrap before every
# credential-reading process. Only explicit dev services may have build steps.
config_root="$(mktemp -d)"
trap 'rm -rf "$config_root"' EXIT HUP INT TERM
version="$(tr -d '\r\n' < "$repo_root/VERSION")"
compose_json="$(AGENT_STUDIO_VERSION="v$version" docker compose \
    --project-directory "$config_root" -f "$repo_root/docker-compose.yml" \
    --profile dev --profile runner --profile edge config --format json)"
node -e '
const config = JSON.parse(process.argv[1]);
const required = ["bootstrap", "task-server", "orchestrator-engine", "studio-bff", "orchestrator-api", "web", "agent-host"];
for (const name of required) {
  const service = config.services[name];
  if (!service || service.profiles?.length) throw new Error(`${name} is not in the default stack`);
  if (name !== "bootstrap" && !service.image.endsWith(":v" + process.argv[2])) {
    throw new Error(`${name} does not use the pinned release tag`);
  }
  if (service.build) throw new Error(`${name} unexpectedly builds from source`);
}
for (const [name, service] of Object.entries(config.services)) {
  if (service.build && !service.profiles?.includes("dev")) throw new Error(`${name} build is not dev-only`);
}
const bootstrap = config.services.bootstrap;
if (!bootstrap.volumes.some(v => v.target === "/run/secrets" && !v.read_only)) {
  throw new Error("bootstrap does not own the credentials volume");
}
for (const name of ["task-server", "studio-bff", "agent-host", "orchestrator-engine"]) {
  const volume = config.services[name].volumes.find(v => v.target === "/run/secrets");
  if (!volume?.read_only) throw new Error(`${name} does not read credentials read-only`);
}
if (config.services["task-server"].depends_on.bootstrap?.condition !== "service_completed_successfully") {
  throw new Error("Task Server does not wait for bootstrap");
}
if (!config.services["orchestrator-engine"].depends_on["task-server"]?.condition.includes("healthy")) {
  throw new Error("Engine does not wait for Task Server health");
}
' "$compose_json" "$version"

printf 'Container image user, health, bootstrap, and entrypoint contracts passed.\n'
