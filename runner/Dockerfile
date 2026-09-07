# agent-host (coding/review runner) with the coding CLIs baked in. Running the
# CLI inside this container is the container-default execution boundary: the
# container is the isolation, the CLI itself runs unsandboxed within it.
# Build context is the repository root: docker build -f runner/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
# VERSION is OCI label material only; the assembly version always comes from the
# repository VERSION file through Directory.Build.props. REVISION replaces the
# csproj git probe, which cannot run because .dockerignore excludes .git, and it
# is what `agent-host --version` reports.
ARG VERSION=
ARG REVISION=unknown
WORKDIR /src
COPY . ./
RUN test -z "$VERSION" || test "$VERSION" = "$(tr -d '\r\n' < VERSION)"
RUN dotnet publish runner/AgentRunner.csproj -c Release -o /app \
        /p:UseAppHost=false \
        /p:SourceRevisionId="$REVISION"

# SDK base image on purpose: coding agents build and test the projects they
# work on, so the execution container needs the full toolchain.
FROM mcr.microsoft.com/dotnet/sdk:10.0
ARG VERSION=
ARG REVISION=unknown
LABEL org.opencontainers.image.title="agent-host" \
      org.opencontainers.image.description="Agent Studio coding and review runner with the CLI toolchain" \
      org.opencontainers.image.version="$VERSION" \
      org.opencontainers.image.revision="$REVISION" \
      org.opencontainers.image.source="https://github.com/agent-orc/agent-studio" \
      org.opencontainers.image.vendor="agent-orc" \
      org.opencontainers.image.licenses="Apache-2.0"
RUN apt-get update \
 && apt-get install -y --no-install-recommends git openssh-client curl ca-certificates \
 && curl -fsSL https://deb.nodesource.com/setup_22.x | bash - \
 && apt-get install -y --no-install-recommends nodejs \
 && npm install --global @openai/codex @anthropic-ai/claude-code \
 && rm -rf /var/lib/apt/lists/* \
 && git config --system safe.directory '*'
WORKDIR /opt/agent-host
COPY --from=build /app ./
# The SDK base image ships no non-root account, so create one. UID 1654 matches
# the APP_UID the .NET runtime images use, which keeps bind-mount ownership
# consistent across the stack. The globally installed CLIs stay root-owned and
# read-only; only the state volume and this account's HOME are writable.
RUN groupadd --gid 1654 agent-host \
 && useradd --uid 1654 --gid 1654 --create-home --home-dir /home/agent-host agent-host \
 && mkdir -p /var/lib/agent-host/runner-work \
             /var/lib/agent-host/state \
             /var/lib/agent-host/home \
 && chown -R 1654:1654 /var/lib/agent-host /home/agent-host
# Variable names follow deploy/release/agent-host/runner.env.template; identity,
# tokens, and git remotes come from the compose env file, never from the image.
ENV RUNNER_SERVER_URL=http://orchestrator-api:5031 \
    RUNNER_ROLE=coding \
    RUNNER_WORKDIR=/var/lib/agent-host/runner-work \
    RUNNER_STATE_DIR=/var/lib/agent-host/state
# HOME lives on the state volume so CLI credentials and caches survive a restart
# and stay off the image layer. DOTNET_CLI_HOME and NUGET_PACKAGES follow it so
# a build the coding agent starts never needs a root-owned path.
ENV HOME=/var/lib/agent-host/home \
    DOTNET_CLI_HOME=/var/lib/agent-host/home \
    NUGET_PACKAGES=/var/lib/agent-host/home/.nuget/packages \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1
# Bounded logs need both halves: the image caps severity, the Compose json-file
# driver caps retained bytes. Either one alone still fills a long-running host.
ENV Logging__LogLevel__Default=Information
VOLUME ["/var/lib/agent-host"]
USER 1654
# agent-host serves no HTTP port of its own, so its liveness contract is
# "configuration parses and the control plane answers" (exit 0 reachable,
# 4 unreachable). It claims no task and touches no lease.
HEALTHCHECK --interval=30s --timeout=15s --start-period=20s --retries=4 \
    CMD ["dotnet", "/opt/agent-host/agent-host.dll", "--health-check"]
ENTRYPOINT ["dotnet", "/opt/agent-host/agent-host.dll", "--poll"]
