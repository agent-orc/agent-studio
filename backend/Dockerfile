# Agent Studio orchestrator API (the current monolith: board API, pipeline,
# review plane, local project registry).
# Build context is the repository root: docker build -f backend/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG VERSION=0.0.0-dev
ARG SHA=unknown
WORKDIR /src
COPY . ./
RUN dotnet publish backend/OrchestratorApi.csproj -c Release -o /app /p:UseAppHost=false \
    -p:Version="$VERSION" -p:SourceRevisionId="$SHA"

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG VERSION=0.0.0-dev
ARG SHA=unknown
LABEL org.opencontainers.image.title="agent-studio-api" \
      org.opencontainers.image.version="$VERSION" \
      org.opencontainers.image.revision="$SHA" \
      org.opencontainers.image.source="https://github.com/agent-orc/agent-studio"
# curl: Compose uses the public health endpoint as its readiness contract.
# git: GitService shells out for merges, worktrees, and integration pushes.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl git openssh-client \
 && rm -rf /var/lib/apt/lists/* \
 && git config --system safe.directory '*' \
 && groupadd --system --gid 10001 app \
 && useradd --system --uid 10001 --gid app --home-dir /nonexistent --shell /usr/sbin/nologin app
WORKDIR /app
COPY --from=build /app ./
# Defense in depth: machine-local overrides must never reach the image even if
# the build context leaks them past .dockerignore.
RUN rm -f /app/appsettings.Local.json /app/appsettings.Development.json
# Runtime prompt material read via AgentRules:CorePath (relative to content root).
COPY agent-rules/ ./agent-rules/
ENV ASPNETCORE_URLS=http://0.0.0.0:5031 \
    TaskRepository=/data/workspace
# /data/workspace: task repository (cards, metadata). /data/projects: project checkouts.
RUN mkdir -p /data/workspace /data/projects && chown -R app:app /app /data
VOLUME ["/data/workspace", "/data/projects"]
EXPOSE 5031
USER app
HEALTHCHECK --interval=5s --timeout=3s --start-period=10s --retries=24 \
    CMD curl --fail --silent http://127.0.0.1:5031/healthz || exit 1
ENTRYPOINT ["dotnet", "OrchestratorApi.dll"]
