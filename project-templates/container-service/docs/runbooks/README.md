# Runbooks

## Host operations

`project.yml` declares two runbooks that Studio can run on the deploy host under
Deploy, Runbooks. Each step names a script from [deploy/runbooks](../../deploy/runbooks)
that the operator installs on the host; Studio never sends a command line.
[docs/deployments](../deployments/README.md#runbooks) explains the install.

| Runbook | Script | What it does |
|---|---|---|
| `restart` | `deploy/runbooks/restart.sh` | `docker compose restart` of the `app` service for the environment, then its state |
| `disk-cleanup` | `deploy/runbooks/disk-cleanup.sh` | Prunes stopped containers, old images and build cache, vacuums the journal; volumes stay |

Both verify the `http` health check afterwards, so a run fails when `/health`
does not answer `200`.

## Health check fails

1. Open Deploy, Operations, and search the logs of the environment for
   `Health check failed`.
2. Call `GET /health` and read the `checks` entry whose status is `down`; its
   `detail` names the HTTP status or that the dependency did not answer.
3. Fix or restart the dependency. Run the `restart` runbook only when the
   service itself is stuck.
4. If a release caused it, roll back from the release in Deploy.

## No telemetry in Operations

1. Check that the environment has `telemetry: true` in `.agent-studio/project.yml`
   and that a release with that setting was deployed.
2. `GET /health` must list `telemetry` as `ok`. `disabled` means the container
   has no `OTEL_EXPORTER_OTLP_ENDPOINT`; redeploy.
3. Open Deploy, Telemetry. The "Managed by deploys" token should be active with
   a recent last use. If it was revoked, redeploy to get a new one.
4. If the token is active but never used, the container cannot reach the
   endpoint. Ask the operator to set `Telemetry:PublicOtlpEndpoint` to an address
   the workload can reach and redeploy.
