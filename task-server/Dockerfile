# Standalone Task Server (v1 run/review plane) - target-architecture component.
# Build context is the repository root: docker build -f task-server/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
# VERSION is OCI label material only; the assembly version always comes from the
# repository VERSION file through Directory.Build.props. REVISION replaces the
# csproj git probe, which cannot run because .dockerignore excludes .git, and it
# is what `task-server --version` reports as TaskServerBuildIdentity.
ARG VERSION=
ARG REVISION=unknown
WORKDIR /src
COPY . ./
RUN test -z "$VERSION" || test "$VERSION" = "$(tr -d '\r\n' < VERSION)"
RUN dotnet publish task-server/TaskServer.csproj -c Release -o /app \
        /p:UseAppHost=false \
        /p:SourceRevisionId="$REVISION"

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG VERSION=
ARG REVISION=unknown
LABEL org.opencontainers.image.title="agent-task-server" \
      org.opencontainers.image.description="Agent Studio Task Server, the durable task and orchestration authority" \
      org.opencontainers.image.version="$VERSION" \
      org.opencontainers.image.revision="$REVISION" \
      org.opencontainers.image.source="https://github.com/agent-orc/agent-studio" \
      org.opencontainers.image.vendor="agent-orc" \
      org.opencontainers.image.licenses="Apache-2.0"
# curl: the HEALTHCHECK below is the container's readiness contract.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app ./
# A fresh named volume inherits the ownership of the image directory it covers,
# so this chown is what lets the non-root account own its SQLite store and
# backups. APP_UID is the non-root account the .NET base images ship.
RUN mkdir -p /var/lib/agent-orchestrator/store /var/lib/agent-orchestrator/backup \
 && chown -R "$APP_UID:$APP_UID" /var/lib/agent-orchestrator
# Same bootstrap variables as deploy/release/agent-orchestrator/config/server.env.template.
# AUTH is deliberately not defaulted here: the server refuses AUTH=none on a
# non-loopback LISTEN_URL, so the deployment must supply a real credential.
ENV LISTEN_URL=http://0.0.0.0:5071 \
    STORE_PATH=/var/lib/agent-orchestrator/store \
    BACKUP_PATH=/var/lib/agent-orchestrator/backup
# Bounded logs need both halves: the image caps severity, the Compose json-file
# driver caps retained bytes. Either one alone still fills a long-running host.
ENV Logging__LogLevel__Default=Information
VOLUME ["/var/lib/agent-orchestrator"]
EXPOSE 5071
USER $APP_UID
# /readyz, not /healthz: the server answers /healthz before schema integrity and
# durable lease/fence authority are restored, and a dependent must not start then.
HEALTHCHECK --interval=15s --timeout=3s --start-period=30s --retries=5 \
    CMD curl --fail --silent http://127.0.0.1:5071/readyz || exit 1
# AssemblyName is `task-server` (task-server/TaskServer.csproj), not TaskServer.
ENTRYPOINT ["dotnet", "task-server.dll"]
