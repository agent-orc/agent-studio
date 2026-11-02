FROM node:22-alpine@sha256:fe4f6d74e20370acb04ad889077d14d0f64a9c8ea6a42bc60cddf1e844699914
ARG HARNESS_REVISION=unknown
ARG HARNESS_IDENTITY=unset
LABEL org.opencontainers.image.revision="${HARNESS_REVISION}" \
      com.agentstudio.remote-harness.identity="${HARNESS_IDENTITY}"
WORKDIR /harness
COPY tools/remote-test-suite/compose-fault-proxy.mjs ./
ENTRYPOINT ["node"]
