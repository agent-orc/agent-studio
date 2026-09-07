# Agent Studio orchestrator API (the current monolith: board API, pipeline,
# review plane, local project registry).
# Build context is the repository root: docker build -f backend/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
# VERSION is OCI label material only; the assembly version always comes from the
# repository VERSION file through Directory.Build.props. REVISION replaces the
# csproj git probe, which cannot run because .dockerignore excludes .git.
ARG VERSION=
ARG REVISION=unknown
WORKDIR /src
COPY . ./
RUN test -z "$VERSION" || test "$VERSION" = "$(tr -d '\r\n' < VERSION)"
RUN dotnet publish backend/OrchestratorApi.csproj -c Release -o /app \
        /p:UseAppHost=false \
        /p:SourceRevisionId="$REVISION"

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG VERSION=
ARG REVISION=unknown
LABEL org.opencontainers.image.title="agent-studio-api" \
      org.opencontainers.image.description="Agent Studio orchestrator API" \
      org.opencontainers.image.version="$VERSION" \
      org.opencontainers.image.revision="$REVISION" \
      org.opencontainers.image.source="https://github.com/agent-orc/agent-studio" \
      org.opencontainers.image.vendor="agent-orc" \
      org.opencontainers.image.licenses="Apache-2.0"
# curl: Compose uses the public health endpoint as its readiness contract.
# git: GitService shells out for merges, worktrees, and integration pushes.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl git openssh-client \
 && rm -rf /var/lib/apt/lists/* \
 && git config --system safe.directory '*'
WORKDIR /app
COPY --from=build /app ./
# Defense in depth: machine-local overrides must never reach the image even if
# the build context leaks them past .dockerignore.
RUN rm -f /app/appsettings.Local.json /app/appsettings.Development.json
# Runtime prompt material read via AgentRules:CorePath (relative to content root).
COPY agent-rules/ ./agent-rules/
# A fresh named volume inherits the ownership of the image directory it covers,
# so these two chowns are what let the non-root account write task and project
# data. APP_UID is the non-root account the .NET base images ship.
RUN mkdir -p /data/workspace /data/projects /home/app \
 && chown -R "$APP_UID:$APP_UID" /data /home/app
ENV ASPNETCORE_URLS=http://0.0.0.0:5031 \
    TaskRepository=/data/workspace \
    HOME=/home/app \
    DOTNET_CLI_HOME=/home/app
# Bounded logs need both halves: the image caps severity, the Compose json-file
# driver caps retained bytes. Either one alone still fills a long-running host.
ENV Logging__LogLevel__Default=Information
# /data/workspace: task repository (cards, metadata). /data/projects: project checkouts.
VOLUME ["/data/workspace", "/data/projects"]
EXPOSE 5031
USER $APP_UID
HEALTHCHECK --interval=15s --timeout=3s --start-period=30s --retries=5 \
    CMD curl --fail --silent http://127.0.0.1:5031/healthz || exit 1
ENTRYPOINT ["dotnet", "OrchestratorApi.dll"]
