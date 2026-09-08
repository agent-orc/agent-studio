# agent-host (coding/review runner) with the coding CLIs baked in. Running the
# CLI inside this container is the container-default execution boundary: the
# container is the isolation, the CLI itself runs unsandboxed within it.
# Build context is the repository root: docker build -f runner/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG VERSION=0.0.0-dev
ARG SHA=unknown
WORKDIR /src
COPY . ./
RUN dotnet publish runner/AgentRunner.csproj -c Release -o /app /p:UseAppHost=false \
    -p:Version="$VERSION" -p:SourceRevisionId="$SHA"

# SDK base image on purpose: coding agents build and test the projects they
# work on, so the execution container needs the full toolchain.
FROM mcr.microsoft.com/dotnet/sdk:10.0
ARG VERSION=0.0.0-dev
ARG SHA=unknown
LABEL org.opencontainers.image.title="agent-host" \
      org.opencontainers.image.version="$VERSION" \
      org.opencontainers.image.revision="$SHA" \
      org.opencontainers.image.source="https://github.com/agent-orc/agent-studio"
RUN apt-get update \
 && apt-get install -y --no-install-recommends git openssh-client curl ca-certificates \
 && curl -fsSL https://deb.nodesource.com/setup_22.x | bash - \
 && apt-get install -y --no-install-recommends nodejs \
 && npm install --global @openai/codex @anthropic-ai/claude-code \
 && rm -rf /var/lib/apt/lists/* \
 && git config --system safe.directory '*' \
 && groupadd --gid 10001 runner \
 && useradd --uid 10001 --gid runner --create-home --home-dir /home/runner --shell /bin/bash runner
WORKDIR /opt/agent-host
COPY --from=build /app ./
# Variable names follow deploy/release/agent-host/runner.env.template; identity,
# tokens, and git remotes come from the compose env file, never from the image.
ENV RUNNER_SERVER_URL=http://orchestrator-api:5031 \
    RUNNER_ROLE=coding \
    RUNNER_WORKDIR=/var/lib/agent-host/runner-work \
    RUNNER_STATE_DIR=/var/lib/agent-host/state \
    HOME=/home/runner
RUN mkdir -p /var/lib/agent-host && chown -R runner:runner /opt/agent-host /var/lib/agent-host /home/runner
VOLUME ["/var/lib/agent-host"]
USER runner
# No HTTP surface (this is a poll-based worker): reuse the existing
# --health-check probe, which proves the container can reach its Task Server.
HEALTHCHECK --interval=15s --timeout=10s --start-period=20s --retries=6 \
    CMD ["dotnet", "/opt/agent-host/agent-host.dll", "--health-check"]
ENTRYPOINT ["dotnet", "/opt/agent-host/agent-host.dll", "--poll"]
