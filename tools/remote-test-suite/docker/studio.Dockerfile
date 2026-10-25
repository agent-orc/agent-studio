FROM node:22-alpine AS build
WORKDIR /src
COPY frontend/package.json frontend/package-lock.json ./
COPY frontend/scripts/ ./scripts/
RUN corepack npm ci
COPY frontend/ ./
RUN corepack npm exec -- ng build frontend \
 && (test -d dist/frontend/browser && mv dist/frontend/browser /studio-dist || mv dist/frontend /studio-dist)

FROM caddy:2-alpine
ARG HARNESS_REVISION=unknown
ARG HARNESS_IDENTITY=unset
LABEL org.opencontainers.image.revision="${HARNESS_REVISION}" \
      com.agentstudio.remote-harness.identity="${HARNESS_IDENTITY}"
COPY tools/remote-test-suite/compose-studio.Caddyfile /etc/caddy/Caddyfile
COPY --from=build /studio-dist /srv/studio
EXPOSE 80
