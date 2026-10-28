# DP-S3 role image for fresh per-attempt work containers (coding, review and
# gate). The host agent runs it by digest only, never by tag, as user 10001
# with a read-only root, no host container socket, a writable attempt volume
# at /work and versioned npm, NuGet and repository-mirror caches below /cache.
# Build context is this directory: docker build -f deploy/work-container/Dockerfile deploy/work-container
FROM mcr.microsoft.com/dotnet/sdk:10.0
ARG VERSION=0.0.0-dev
ARG SHA=unknown
ARG CODEX_CLI_VERSION=0.154.0
ARG CLAUDE_CLI_VERSION=2.1.269
LABEL org.opencontainers.image.title="agent-work" \
      org.opencontainers.image.version="$VERSION" \
      org.opencontainers.image.revision="$SHA" \
      org.opencontainers.image.source="https://github.com/agent-orc/agent-studio"
RUN apt-get update \
 && apt-get install -y --no-install-recommends git curl ca-certificates \
 && curl -fsSL https://deb.nodesource.com/setup_22.x | bash - \
 && apt-get install -y --no-install-recommends nodejs \
 && npm install --global \
      "@openai/codex@${CODEX_CLI_VERSION}" \
      "@anthropic-ai/claude-code@${CLAUDE_CLI_VERSION}" \
 && npm cache clean --force \
 && rm -rf /var/lib/apt/lists/* \
 && groupadd --gid 10001 runner \
 && useradd --uid 10001 --gid runner --no-create-home --home-dir /work/home --shell /bin/sh runner \
 && mkdir -p /work /cache/npm /cache/nuget /cache/repo \
 && chown -R 10001:10001 /work /cache
# The host may bind a read-only bare source repository owned by another uid.
RUN git config --system --add safe.directory /mnt/source
COPY git-credential-helper.sh /usr/local/bin/agent-git-credential-helper
RUN chmod 0755 /usr/local/bin/agent-git-credential-helper
# No VOLUME declaration: the host agent mounts named volumes on every path, and
# an empty named volume copies this ownership so user 10001 can write it.
ENV HOME=/work/home \
    npm_config_cache=/cache/npm \
    NUGET_PACKAGES=/cache/nuget \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1
WORKDIR /work
USER runner
# A work container is one bounded command, not a service; the host agent
# observes its exit code, flushed log and cleanup instead of a health probe.
HEALTHCHECK NONE
ENTRYPOINT []
CMD ["/bin/sh", "-c", "echo 'agent-work runs only as a host-agent attempt' >&2; exit 64"]
