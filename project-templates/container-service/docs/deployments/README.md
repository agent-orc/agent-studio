# Container deployment

Run `npm ci`, `npm test`, and `npm run build`. Build with `docker build -t service .`.
The container listens on port 3000.

## Connect the deploy

`.agent-studio/project.yml` uses schema 2. Before the first release, replace the
placeholders in its `deploy` section:

1. `target: app-host`: the id of the deploy host registered in Studio.
2. `composeFile`: where [deploy/compose.yml](../../deploy/compose.yml) lives on that
   host for staging and for production.
3. `healthUrl`: the addresses your proxy serves `/health` at.

Staging deploys automatically after integration; production waits for an Owner
or Maintainer approval.

## Health

`GET /health` answers `200` with `{"status":"ok","checks":[...]}` while every
dependency answers, and `503` with `"status":"down"` when one does not. The
checks list the process, whether telemetry is configured, and each dependency
named in `HEALTH_DEPENDENCIES` (comma-separated `name=url` pairs) with its
latency or the reason it failed. Telemetry never fails the check.

## Telemetry

Both environments set `telemetry: true`. Each deploy then receives
`OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_EXPORTER_OTLP_PROTOCOL`,
`OTEL_EXPORTER_OTLP_HEADERS` (an ingest token scoped to the environment) and
`OTEL_RESOURCE_ATTRIBUTES` (environment and release id). `telemetry.mjs` starts the
OpenTelemetry Node SDK with HTTP server metrics, Node.js runtime metrics, process
CPU and memory, and log export. Studio shows them under Deploy, Operations
(trends and log search). Studio manages the token; the Telemetry tab lists it
as "Managed by deploys" with its last use, and a revoked token is replaced at
the next deploy. Do not set the `OTEL_EXPORTER_OTLP_*` variables yourself.

Without an endpoint, for example in local runs, the service starts without telemetry.

## Runbooks

The named operations in `runbooks` are described in [docs/runbooks](../runbooks/README.md).
