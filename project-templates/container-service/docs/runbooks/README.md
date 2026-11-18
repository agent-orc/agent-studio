# Runbooks

## Host operations

`project.yml` declares two runbooks that Studio can run on the deploy host under
Deploy, Runbooks. Each step runs a script from `deploy/runbooks` that the operator
installs on the host ([install](../deployments/README.md#runbooks)); Studio never
sends a command line. After the scripts succeed, Studio probes the `http` health
check and fails the run when it does not pass.

| Runbook | Script | Use it when |
|---|---|---|
| `restart` | `deploy/runbooks/restart.sh` | The service itself is stuck; restarts the `app` container |
| `disk-cleanup` | `deploy/runbooks/disk-cleanup.sh` | The host runs out of disk; prunes stopped containers, unused images and old build cache |

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
