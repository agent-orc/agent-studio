# Container deployment

Run `npm ci`, `npm test`, and `npm run build`. Build with `docker build -t service .`, then publish the image to your registry. The container listens on port 3000 and exposes `/health`.

## Runbooks

The commented schema 2 section of `.agent-studio/project.yml` declares two runbooks:

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

The scripts read `AGENT_STUDIO_PROJECT` and `AGENT_STUDIO_ENVIRONMENT` from the host. When your compose file or service name differs from `/srv/apps/<project>/<staging|production>/compose.yml` and `<project>`, declare `RUNBOOK_COMPOSE_FILE` and `RUNBOOK_SERVICE` as project.yml variables scoped to each environment. The user that runs the deploy host needs access to the Docker socket.

## Verification

A script that exits 0 has not proven that the application works. Each runbook sets `verify: health:http`, so Studio probes the environment's `http` health check after the scripts succeed and marks the run failed when the check does not pass. The run log names the check, its URL and what it answered.

`/health` of this template only says that the process runs. When the service depends on a database or another API, declare a deeper health check under `healthChecks` in the environment declaration, for example a `ready` check on a URL that exercises those dependencies, and point the runbooks at it with `verify: health:ready`. A telemetry alert rule works too: `verify: alert:5xx-rate` passes only when the rule is observed and not firing.
