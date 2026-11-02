# Agent Studio orchestrator API (the current monolith: board API, pipeline,
# review plane, local project registry).
# Build context is the repository root: docker build -f backend/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0.301 AS build
ARG VERSION=0.0.0-dev
ARG SHA=unknown
WORKDIR /src
COPY . ./
RUN dotnet publish backend/OrchestratorApi.csproj -c Release -o /app /p:UseAppHost=false \
    -p:Version="$VERSION" -p:SourceRevisionId="$SHA"

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG VERSION=0.0.0-dev
ARG SHA=unknown
ARG CODEX_CLI_VERSION=0.166.0
ARG CLAUDE_CLI_VERSION=2.1.305
ARG GEMINI_CLI_VERSION=0.39.1
LABEL org.opencontainers.image.title="agent-studio-api" \
      org.opencontainers.image.version="$VERSION" \
      org.opencontainers.image.revision="$SHA" \
      org.opencontainers.image.source="https://github.com/agent-orc/agent-studio"
# curl: the container probe checks local readiness independently of public login diagnostics.
# git: GitService shells out for merges, worktrees, and integration pushes.
# user.name/user.email: the default committer identity of the service user
# (AGT-3412). System level, because HOME (/data/home) is a mounted volume that
# hides anything the image writes there; a --global identity on it still wins.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl git openssh-client ca-certificates \
 && curl -fsSL https://deb.nodesource.com/setup_22.x | bash - \
 && apt-get install -y --no-install-recommends nodejs \
 && npm install --global \
      "@openai/codex@${CODEX_CLI_VERSION}" \
      "@anthropic-ai/claude-code@${CLAUDE_CLI_VERSION}" \
      "@google/gemini-cli@${GEMINI_CLI_VERSION}" \
 && rm -rf /var/lib/apt/lists/* \
 && git config --system safe.directory '*' \
 && git config --system user.name 'Agent Studio' \
 && git config --system user.email 'studio@agent-studio.invalid' \
 && groupadd --system --gid 10001 agentstudio \
 && useradd --system --uid 10001 --gid agentstudio --home-dir /data/home --shell /usr/sbin/nologin agentstudio
WORKDIR /app
COPY --from=build /app ./
# Defense in depth: machine-local overrides must never reach the image even if
# the build context leaks them past .dockerignore.
RUN rm -f /app/appsettings.Local.json /app/appsettings.Development.json
# Runtime prompt material read via AgentRules:CorePath (relative to content root).
COPY agent-rules/ ./agent-rules/
COPY scripts/monolith-seed-cli-auth.sh /usr/local/bin/monolith-seed-cli-auth.sh
ENV ASPNETCORE_URLS=http://0.0.0.0:5031 \
    TaskRepository=/data/workspace
# /data/workspace: task repository (cards, metadata). /data/projects: project checkouts.
RUN mkdir -p /data/workspace /data/projects /data/home && chown -R agentstudio:agentstudio /app /data
VOLUME ["/data/workspace", "/data/projects"]
EXPOSE 5031
USER agentstudio
HEALTHCHECK --interval=5s --timeout=3s --start-period=10s --retries=24 \
    CMD curl --fail --silent -H "Host: ${STUDIO_DOMAIN:-localhost}" http://127.0.0.1:5031/healthz/container || exit 1
ENTRYPOINT ["dotnet", "OrchestratorApi.dll"]
