FROM node:22-alpine@sha256:fe4f6d74e20370acb04ad889077d14d0f64a9c8ea6a42bc60cddf1e844699914 AS build
WORKDIR /src
COPY frontend/package.json frontend/package-lock.json ./
COPY frontend/scripts/ ./scripts/
RUN corepack npm ci
COPY frontend/ ./
RUN corepack npm exec -- ng build frontend \
 && (test -d dist/frontend/browser && mv dist/frontend/browser /studio-dist || mv dist/frontend /studio-dist)

FROM caddy:2-alpine@sha256:e70b164da81ef698b3ad19af799ea6086ce6d9e48ed4079eb9fe26f6fbf14ee5
ARG HARNESS_REVISION=unknown
ARG HARNESS_IDENTITY=unset
LABEL org.opencontainers.image.revision="${HARNESS_REVISION}" \
      com.agentstudio.remote-harness.identity="${HARNESS_IDENTITY}"
COPY tools/remote-test-suite/compose-studio.Caddyfile /etc/caddy/Caddyfile
COPY --from=build /studio-dist /srv/studio
EXPOSE 80
