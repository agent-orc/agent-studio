FROM node:22-alpine
WORKDIR /app
COPY package*.json ./
RUN npm ci --omit=dev
COPY server.mjs health.mjs logger.mjs telemetry.mjs telemetry-config.mjs ./
ENV PORT=3000 NODE_ENV=production
EXPOSE 3000
HEALTHCHECK CMD node -e "fetch('http://127.0.0.1:3000/health').then(r=>process.exit(r.ok?0:1)).catch(()=>process.exit(1))"
# telemetry.mjs starts the OpenTelemetry SDK when the deploy sets OTEL_EXPORTER_OTLP_ENDPOINT.
CMD ["node", "--import", "./telemetry.mjs", "server.mjs"]
