# agent-host (coding/review runner) with the coding CLIs baked in. Running the
# CLI inside this container is the container-default execution boundary: the
# container is the isolation, the CLI itself runs unsandboxed within it.
# Build context is the repository root: docker build -f runner/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG VERSION=0.0.0-local
ARG COMMIT=unknown
WORKDIR /src
COPY . ./
RUN dotnet publish runner/AgentRunner.csproj -c Release -o /app /p:UseAppHost=false \
    -p:Version="$VERSION" -p:SourceRevisionId="$COMMIT"

# SDK base image on purpose: coding agents build and test the projects they
# work on, so the execution container needs the full toolchain.
FROM mcr.microsoft.com/dotnet/sdk:10.0
ARG VERSION=0.0.0-local
ARG COMMIT=unknown
ARG BUILD_IDENTITY=agent-host/0.0.0-local+sha.unknown
LABEL org.opencontainers.image.title="Agent Host" \
      org.opencontainers.image.version="$VERSION" \
      org.opencontainers.image.revision="$COMMIT" \
      io.agent-studio.build-identity="$BUILD_IDENTITY"
RUN apt-get update \
 && apt-get install -y --no-install-recommends git openssh-client curl ca-certificates \
 && curl -fsSL https://deb.nodesource.com/setup_22.x | bash - \
 && apt-get install -y --no-install-recommends nodejs \
 && npm install --global @openai/codex @anthropic-ai/claude-code \
 && rm -rf /var/lib/apt/lists/* \
 && git config --system safe.directory '*' \
 && groupadd --system --gid 10001 agent-host \
 && useradd --system --uid 10001 --gid agent-host --home-dir /var/lib/agent-host --create-home --shell /bin/bash agent-host
WORKDIR /opt/agent-host
COPY --from=build /app ./
# Variable names follow deploy/release/agent-host/runner.env.template; identity,
# tokens, and git remotes come from the compose env file, never from the image.
ENV RUNNER_SERVER_URL=http://orchestrator-api:5031 \
    RUNNER_ROLE=coding \
    RUNNER_WORKDIR=/var/lib/agent-host/runner-work \
    RUNNER_STATE_DIR=/var/lib/agent-host/state
RUN mkdir -p /var/lib/agent-host/runner-work /var/lib/agent-host/state \
 && chown -R agent-host:agent-host /opt/agent-host /var/lib/agent-host
VOLUME ["/var/lib/agent-host"]
USER 10001:10001
HEALTHCHECK --interval=30s --timeout=10s --start-period=20s --retries=3 \
  CMD dotnet /opt/agent-host/agent-host.dll --health-check || exit 1
ENTRYPOINT ["dotnet", "/opt/agent-host/agent-host.dll", "--poll"]
