# Orchestrator engine (headless worker against the Task Server) - target-architecture component.
# Build context is the repository root: docker build -f orchestrator-engine/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG VERSION=0.0.0-local
ARG COMMIT=unknown
WORKDIR /src
COPY . ./
RUN dotnet publish orchestrator-engine/OrchestratorEngine.csproj -c Release -o /app /p:UseAppHost=false \
    -p:Version="$VERSION" -p:SourceRevisionId="$COMMIT"

FROM mcr.microsoft.com/dotnet/runtime:10.0
ARG VERSION=0.0.0-local
ARG COMMIT=unknown
ARG BUILD_IDENTITY=orchestrator-engine/0.0.0-local+sha.unknown
LABEL org.opencontainers.image.title="Agent Orchestrator Engine" \
      org.opencontainers.image.version="$VERSION" \
      org.opencontainers.image.revision="$COMMIT" \
      io.agent-studio.build-identity="$BUILD_IDENTITY"
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/* \
 && groupadd --system --gid 10001 agent-studio \
 && useradd --system --uid 10001 --gid agent-studio --home-dir /nonexistent --shell /usr/sbin/nologin agent-studio
WORKDIR /app
COPY --from=build /app ./
ENV SERVER_URL=http://task-server:5071
USER 10001:10001
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
  CMD curl --fail --silent "$SERVER_URL/healthz" >/dev/null || exit 1
ENTRYPOINT ["dotnet", "OrchestratorEngine.dll"]
