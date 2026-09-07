# Agent Studio frontend: production Angular build served statically by Caddy,
# API calls reverse-proxied to the orchestrator API.
# Build context is the repository root: docker build -f frontend/Dockerfile .
FROM node:22-alpine AS build
WORKDIR /src
COPY frontend/package.json frontend/package-lock.json ./
# postinstall runs scripts/patch-coding-agent-chat-model-selector.mjs, so the
# scripts folder must be present in the restore layer already.
COPY frontend/scripts/ ./scripts/
RUN npm ci
COPY frontend/ ./
# Angular's application builder emits dist/frontend/browser; older layouts
# emit dist/frontend directly - normalise both into /studio-dist.
RUN npx ng build frontend \
 && (test -d dist/frontend/browser && mv dist/frontend/browser /studio-dist || mv dist/frontend /studio-dist)

FROM caddy:2-alpine
ARG VERSION=
ARG REVISION=unknown
LABEL org.opencontainers.image.title="agent-studio-web" \
      org.opencontainers.image.description="Agent Studio Angular bundle served by Caddy" \
      org.opencontainers.image.version="$VERSION" \
      org.opencontainers.image.revision="$REVISION" \
      org.opencontainers.image.source="https://github.com/agent-orc/agent-studio" \
      org.opencontainers.image.vendor="agent-orc" \
      org.opencontainers.image.licenses="Apache-2.0"
COPY deploy/compose/Caddyfile /etc/caddy/Caddyfile
COPY --from=build /studio-dist /srv/studio
# Caddy keeps its certificate and autosave state under the XDG paths the base
# image declares; a non-root server needs both of them writable.
RUN addgroup -S studio \
 && adduser -S -D -H -G studio studio \
 && mkdir -p /data/caddy /config/caddy \
 && chown -R studio:studio /data/caddy /config/caddy
# Port 8080, not 80: an unprivileged process cannot bind a privileged port, and
# granting CAP_NET_BIND_SERVICE back would defeat the non-root requirement.
# The published host port is unchanged; only the in-container port moved.
EXPOSE 8080
USER studio
HEALTHCHECK --interval=15s --timeout=3s --start-period=10s --retries=5 \
    CMD wget --quiet --output-document=- http://127.0.0.1:8080/healthz || exit 1
