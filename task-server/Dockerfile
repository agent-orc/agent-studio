# Standalone Task Server (v1 run/review plane) - target-architecture component.
# Build context is the repository root: docker build -f task-server/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG VERSION=0.0.0-local
ARG COMMIT=unknown
WORKDIR /src
COPY . ./
RUN dotnet publish task-server/TaskServer.csproj -c Release -o /app /p:UseAppHost=false \
    -p:Version="$VERSION" -p:SourceRevisionId="$COMMIT"

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG VERSION=0.0.0-local
ARG COMMIT=unknown
ARG BUILD_IDENTITY=task-server/0.0.0-local+sha.unknown
LABEL org.opencontainers.image.title="Agent Task Server" \
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
# Same bootstrap variables as deploy/release/agent-orchestrator/config/server.env.template.
ENV LISTEN_URL=http://0.0.0.0:5071 \
    STORE_PATH=/var/lib/agent-orchestrator/store \
    BACKUP_PATH=/var/lib/agent-orchestrator/backup
RUN mkdir -p /var/lib/agent-orchestrator/store /var/lib/agent-orchestrator/backup \
 && chown -R agent-studio:agent-studio /app /var/lib/agent-orchestrator
VOLUME ["/var/lib/agent-orchestrator"]
EXPOSE 5071
USER 10001:10001
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
  CMD curl --fail --silent http://127.0.0.1:5071/healthz >/dev/null || exit 1
ENTRYPOINT ["dotnet", "TaskServer.dll"]
