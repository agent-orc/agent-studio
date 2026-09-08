# Orchestrator engine (headless worker against the Task Server) - target-architecture component.
# Build context is the repository root: docker build -f orchestrator-engine/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG VERSION=0.0.0-dev
ARG SHA=unknown
WORKDIR /src
COPY . ./
RUN dotnet publish orchestrator-engine/OrchestratorEngine.csproj -c Release -o /app /p:UseAppHost=false \
    -p:Version="$VERSION" -p:SourceRevisionId="$SHA"

FROM mcr.microsoft.com/dotnet/runtime:10.0
ARG VERSION=0.0.0-dev
ARG SHA=unknown
LABEL org.opencontainers.image.title="agent-orchestrator-engine" \
      org.opencontainers.image.version="$VERSION" \
      org.opencontainers.image.revision="$SHA" \
      org.opencontainers.image.source="https://github.com/agent-orc/agent-studio"
RUN groupadd --system --gid 10001 app \
 && useradd --system --uid 10001 --gid app --home-dir /nonexistent --shell /usr/sbin/nologin app
WORKDIR /app
COPY --from=build /app ./
RUN chown -R app:app /app
ENV SERVER_URL=http://task-server:5071 \
    HEALTH_PORT=5073
EXPOSE 5073
USER app
# No HTTP surface and no filesystem access (this is a pure Task Server API
# client): the hosted EngineHealthServer accepts a loopback TCP connection on
# HEALTH_PORT, and --health-check treats a successful connect as live.
HEALTHCHECK --interval=10s --timeout=5s --start-period=15s --retries=6 \
    CMD ["dotnet", "OrchestratorEngine.dll", "--health-check"]
ENTRYPOINT ["dotnet", "OrchestratorEngine.dll"]
