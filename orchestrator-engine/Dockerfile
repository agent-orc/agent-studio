# Orchestrator engine (headless worker against the Task Server) - target-architecture component.
# Build context is the repository root: docker build -f orchestrator-engine/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
# VERSION is OCI label material only; the assembly version always comes from the
# repository VERSION file through Directory.Build.props. REVISION replaces the
# csproj git probe, which cannot run because .dockerignore excludes .git, and it
# is what `orchestrator-engine --version` reports.
ARG VERSION=
ARG REVISION=unknown
WORKDIR /src
COPY . ./
RUN test -z "$VERSION" || test "$VERSION" = "$(tr -d '\r\n' < VERSION)"
RUN dotnet publish orchestrator-engine/OrchestratorEngine.csproj -c Release -o /app \
        /p:UseAppHost=false \
        /p:SourceRevisionId="$REVISION"

FROM mcr.microsoft.com/dotnet/runtime:10.0
ARG VERSION=
ARG REVISION=unknown
LABEL org.opencontainers.image.title="agent-orchestrator-engine" \
      org.opencontainers.image.description="Agent Studio API-only flow execution service" \
      org.opencontainers.image.version="$VERSION" \
      org.opencontainers.image.revision="$REVISION" \
      org.opencontainers.image.source="https://github.com/agent-orc/agent-studio" \
      org.opencontainers.image.vendor="agent-orc" \
      org.opencontainers.image.licenses="Apache-2.0"
WORKDIR /app
COPY --from=build /app ./
# Same bootstrap variables as deploy/release/agent-orchestrator/config/engine.env.template.
# ALLOW_INSECURE_HTTP is the explicit opt-in a container network needs, because
# http://task-server:5071 is neither HTTPS nor loopback. CLIENT_CREDENTIAL stays
# required regardless and is never baked into the image.
ENV SERVER_URL=http://task-server:5071 \
    CLIENT_ID=orchestrator-engine
# Bounded logs need both halves: the image caps severity, the Compose json-file
# driver caps retained bytes. Either one alone still fills a long-running host.
ENV Logging__LogLevel__Default=Information
USER $APP_UID
# The Engine serves no HTTP port of its own, so its liveness contract is
# "configuration parses and the control plane answers" - the same shape and exit
# codes as `agent-host --health-check`.
HEALTHCHECK --interval=30s --timeout=15s --start-period=20s --retries=4 \
    CMD ["dotnet", "orchestrator-engine.dll", "--health-check"]
# AssemblyName is `orchestrator-engine` (orchestrator-engine/OrchestratorEngine.csproj).
ENTRYPOINT ["dotnet", "orchestrator-engine.dll"]
