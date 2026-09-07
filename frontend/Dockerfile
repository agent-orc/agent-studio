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
ARG VERSION=0.0.0-local
ARG COMMIT=unknown
ARG BUILD_IDENTITY=agent-studio-web/0.0.0-local+sha.unknown
LABEL org.opencontainers.image.title="Agent Studio Web" \
      org.opencontainers.image.version="$VERSION" \
      org.opencontainers.image.revision="$COMMIT" \
      io.agent-studio.build-identity="$BUILD_IDENTITY"
COPY deploy/compose/Caddyfile /etc/caddy/Caddyfile
COPY --from=build /studio-dist /srv/studio
RUN chown -R caddy:caddy /srv/studio /etc/caddy
EXPOSE 8080
USER caddy
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD wget --quiet --output-document=- http://127.0.0.1:8080/healthz >/dev/null || exit 1
