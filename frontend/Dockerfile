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
ARG VERSION=0.0.0-dev
ARG SHA=unknown
LABEL org.opencontainers.image.title="agent-studio-web" \
      org.opencontainers.image.version="$VERSION" \
      org.opencontainers.image.revision="$SHA" \
      org.opencontainers.image.source="https://github.com/agent-orc/agent-studio"
# Caddy needs to write its autosave/state under XDG dirs and read its config;
# bind an unprivileged port (8080) so the process never needs CAP_NET_BIND_SERVICE.
RUN addgroup -g 10001 -S app \
 && adduser -u 10001 -S -G app -H -h /nonexistent app \
 && mkdir -p /config/caddy /data/caddy \
 && chown -R app:app /config/caddy /data/caddy
ENV XDG_CONFIG_HOME=/config \
    XDG_DATA_HOME=/data
COPY deploy/compose/Caddyfile /etc/caddy/Caddyfile
COPY --from=build /studio-dist /srv/studio
EXPOSE 8080
USER app
HEALTHCHECK --interval=5s --timeout=3s --start-period=5s --retries=12 \
    CMD wget --quiet --output-document=- http://127.0.0.1:8080/healthz || exit 1
