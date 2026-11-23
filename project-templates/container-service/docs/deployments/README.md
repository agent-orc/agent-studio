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

`.agent-studio/project.yml` declares two runbooks:

| Runbook | Script | What it does |
|---|---|---|
| `restart` | `deploy/runbooks/restart.sh` | Restarts the service container with `docker compose restart` and prints its state. |
| `disk-cleanup` | `deploy/runbooks/disk-cleanup.sh` | Removes stopped containers, unused images and build cache older than a week, vacuums the journal, and prints disk usage before and after. Volumes are never touched. |

Studio never sends script text to the deploy host. Each runbook step names a script that an operator installs on the host, under `/etc/agent-host/runbooks/<project>/<step>.sh`, where `<project>` is the project name Studio shows. Install both scripts once per deploy host, as root:

```sh
project=container-service   # the project name in Studio
sudo install -d -m 0755 /etc/agent-host/runbooks/$project
sudo install -m 0755 deploy/runbooks/restart.sh deploy/runbooks/disk-cleanup.sh /etc/agent-host/runbooks/$project/
```

Repeat the install after you change a script; the host runs the installed copy, not the repository file. A step whose script is missing fails, and its log names the path the host looked for.

The scripts read `AGENT_STUDIO_PROJECT` and `AGENT_STUDIO_ENVIRONMENT` from the host. `project.yml` sets `RUNBOOK_SERVICE` to `app`, the service in [deploy/compose.yml](../../deploy/compose.yml). When your compose file differs from `/srv/apps/<project>/<staging|production>/compose.yml`, declare `RUNBOOK_COMPOSE_FILE` as a project.yml variable scoped to each environment. The user that runs the deploy host needs access to the Docker socket.

Diagnosis steps for failing health checks and missing telemetry are in [docs/runbooks](../runbooks/README.md).

## Verification

A script that exits 0 has not proven that the application works. Each runbook sets `verify: health:http`, so Studio probes the environment's `http` health check after the scripts succeed and marks the run failed when the check does not pass. The run log names the check, its URL and what it answered.

`/health` of this template answers `503` when a dependency named in `HEALTH_DEPENDENCIES` does not answer, so the `http` check covers those dependencies. For a deeper check, declare one under `healthChecks` in the environment declaration, for example a `ready` check on a URL that exercises the database, and point the runbooks at it with `verify: health:ready`. A telemetry alert rule works too: `verify: alert:5xx-rate` passes only when the rule is observed and not firing.
