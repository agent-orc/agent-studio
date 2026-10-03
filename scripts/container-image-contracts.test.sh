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

# Repository-root build contexts must never carry local secrets into images.
for pattern in '**/*.env' '**/*.token' '**/.git-credentials' '**/git-credentials'
do
    grep -Fxq -- "$pattern" "$repo_root/.dockerignore" || {
        echo ".dockerignore does not exclude $pattern from build contexts" >&2
        exit 1
    }
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
        --profile ops \
        --profile edge \
        config --format json
)"

EXPECTED_VERSION="$version" node -e '
const config = JSON.parse(process.argv[1]);
const services = config.services;
for (const legacy of ["agent-host-coding", "agent-host-review"]) {
  if (services[legacy]) throw new Error(`${legacy} must not target the closed compatibility protocol`);
}
const secretMount = service => services[service]?.volumes?.find(
  volume => volume.source === "secrets" && volume.target === "/run/agent-studio-secrets");
for (const [service, bootstrap] of [["bootstrap", true], ["bootstrap-dev", true],
  ...["task-server", "orchestrator-engine", "studio-bff", "orchestrator-api", "agent-host-distributed", "agent-host-review-distributed"].map(name => [name, false]),
  ...["task-server-dev", "orchestrator-engine-dev", "studio-bff-dev", "orchestrator-api-dev", "agent-host-distributed-dev", "agent-host-review-distributed-dev"].map(name => [name, false])]) {
  const mount = secretMount(service);
  if (!mount || mount.type !== "volume" || Boolean(mount.read_only) === bootstrap)
    throw new Error(`${service} has an invalid secret volume mount`);
}
for (const service of ["task-server", "orchestrator-engine", "studio-bff", "orchestrator-api", "web", "agent-host-distributed", "agent-host-review-distributed"]) {
  if (services[service].build || !services[service].image?.endsWith(`:v${process.env.EXPECTED_VERSION}`))
    throw new Error(`${service} must use the pinned release image`);
}
for (const service of ["task-server-dev", "orchestrator-engine-dev", "studio-bff-dev", "orchestrator-api-dev", "web-dev", "agent-host-distributed-dev", "agent-host-review-distributed-dev"]) {
  if (!services[service].build)
    throw new Error(`${service} must build from this checkout`);
}
for (const [service, dependency] of [["task-server", "bootstrap"], ["task-server-dev", "bootstrap-dev"]]) {
  if (services[service].depends_on?.[dependency]?.condition !== "service_completed_successfully")
    throw new Error(`${service} must wait for credential bootstrap`);
}
for (const [web, bff] of [["web", "studio-bff"], ["web-dev", "studio-bff-dev"]]) {
  if (services[web].environment?.STUDIO_BFF_UPSTREAM !== `${bff}:5072` ||
      services[web].depends_on?.[bff]?.condition !== "service_healthy")
    throw new Error(`${web} must route distributed traffic to a healthy ${bff}`);
}
for (const [manager, server] of [["credential-manager", "task-server"], ["credential-manager-dev", "task-server-dev"]]) {
  if (!services[manager].profiles?.includes(manager.endsWith("-dev") ? "dev" : "ops") ||
      services[manager].entrypoint?.[1] !== "/opt/compose-rotate-credentials.sh" ||
      secretMount(manager)?.read_only === true)
    throw new Error(`${manager} must own a writable secret mount and be opt-in`);
}
for (const service of ["task-server", "task-server-dev", "web", "web-dev"]) {
  if (services[service].ports?.[0]?.host_ip !== "127.0.0.1")
    throw new Error(`${service} must bind to loopback by default`);
}
for (const service of ["studio-bff", "studio-bff-dev"]) {
  if (!services[service].environment?.Studio__AllowedOrigins)
    throw new Error(`${service} must have an explicit browser Origin allowlist`);
}
for (const service of ["agent-host-review-distributed", "agent-host-review-distributed-dev"]) {
  if (services[service].environment?.RUNNER_ROLE !== "review" ||
      services[service].environment?.RUNNER_AUTH_TOKEN_FILE !== "/run/agent-studio-secrets/review_runner_token")
    throw new Error(`${service} must use the separate review principal`);
}
for (const service of ["orchestrator-api", "orchestrator-api-dev"]) {
  if (services[service].volumes?.some(volume => ["workspace", "projects"].includes(volume.source)))
    throw new Error(`${service} must not retain a compatibility task store`);
  // The compatibility API holds no task workspace; naming one in Compose
  // would advertise a local store that no mounted volume backs.
  if (Object.hasOwn(services[service].environment ?? {}, "TaskRepository"))
    throw new Error(`${service} must not configure a compatibility TaskRepository`);
}
' "$compose_json"

# The operator-facing profile table must name exactly the services each
# non-dev profile starts, so the documented install contract cannot drift.
node -e '
const fs = require("fs");
const services = JSON.parse(process.argv[1]).services;
const doc = fs.readFileSync(process.argv[2], "utf8");
const header = "| Profile | Services | Purpose |";
const rows = doc.slice(doc.indexOf(header)).split("\n").slice(2);
const documented = new Map();
for (const row of rows) {
  if (!row.startsWith("|")) break;
  const [profile, cell] = row.split("|").slice(1, 3).map(part => part.trim());
  if (profile === "`dev`") continue;
  documented.set(profile.replace(/`/g, ""), new Set([...cell.matchAll(/`([^`]+)`/g)].map(match => match[1])));
}
const actual = new Map();
for (const [name, service] of Object.entries(services)) {
  const profile = service.profiles?.[0] ?? "(none)";
  if (profile === "dev") continue;
  if (!actual.has(profile)) actual.set(profile, new Set());
  actual.get(profile).add(name);
}
const render = map => JSON.stringify([...map].map(([key, names]) => [key, [...names].sort()]).sort());
if (render(documented) !== render(actual))
  throw new Error(`task-server.md profile table ${render(documented)} does not match docker-compose.yml ${render(actual)}`);
' "$compose_json" "$repo_root/docs/operations/setup/task-server.md"

grep -F '@distributed path /api/v1 /api/v1/*' "$repo_root/deploy/compose/Caddyfile" >/dev/null
grep -F 'reverse_proxy {$STUDIO_BFF_UPSTREAM:studio-bff:5072}' "$repo_root/deploy/compose/Caddyfile" >/dev/null
grep -F '@api path /api /api/* /healthz /readyz' "$repo_root/deploy/compose/Caddyfile" >/dev/null

printf 'Container image user, health, and entrypoint contracts passed.\n'
