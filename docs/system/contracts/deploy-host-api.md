# Deploy host API contract

The deploy host polls Studio for work created from a release's committed
`.agent-studio/project.yml` schema 2 definition. The release stores the effective
environment declarations and the ordered SSH deploy steps. Legacy
`docs/deployments/environments/*.json` files register hosts; release creation
refuses a declaration that diverges from schema 2. See the
[project deployment definition](../domains/pipeline.md#project-deployment-definition-p1)
for migration and validation.

The API uses JSON with camel-case property names. On a networked installation,
all three routes require HTTPS and a Runner service bearer with `runner.deploy`.
The claim request's `hostId` must equal the enrolled Runner principal's ID.
The report and log routes additionally require the current claim's `leaseToken`;
the token is a claim capability and must stay out of operator logs and UI
responses. The public demo denies these execution routes.

| Method and path | Request | Response |
|---|---|---|
| `POST /api/deploy/jobs/claim` | `{ "hostId": "runner_deploy_1" }` | `200` with one `DeployJob`, or `200` with `null` when no job is claimable. A networked caller with a different enrolled host ID gets `403`. |
| `POST /api/deploy/jobs/{project}/{jobId}/log` | `{ "leaseToken": "...", "step": "deploy[0]:static-site", "message": "..." }` | `200` after appending the line to the deploy run; `409` if validation or the claim fails. |
| `POST /api/deploy/jobs/{project}/{jobId}/report` | `{ "leaseToken": "...", "healthy": true, "evidence": "health-ok", "steps": [{ "name": "deploy[0]:static-site", "succeeded": true }] }` | `200` after closing the job and reconciling the run; `409` for a stale token, invalid step sequence, or a job that is no longer claimed. |

The claim response supplies `id`, `project`, `environmentId`, `hostId`,
`artifact`, `template`, `destination`, `healthUrl`, `secretRefs`, `containerPort`,
`previousArtifact`, `status`, `leaseToken`, timestamps, and claim metadata.
Schema 2 jobs also carry `sshSteps`, `variables`, and `artifacts` from the
release snapshot. Each `sshSteps` item contains its `step` (`static-site` or
`container`), `artifact`, `target`, destination (`directory` or `composeFile`
and `service`), `healthUrl`, `healthTimeoutSeconds`, and environment scope.
Each variable has a `name` and either a plain `value` or a secret *name* in
`secret`; the claim never contains a resolved secret value. The deploy host
uses the snapshot, not the repository's current working tree, to execute the
job. A claim for a release's environment goes to its currently registered host.
The SSH executor requires each snapshotted step's `target` to equal that host.
If host registration changes after the release, an old release's step fails
this check; create a new release with the new target before deploying there.

One host receives one job at a time. A claim is live for five minutes; after
that the same host can reclaim its job and receives a new lease token. A job
without a report or reclaim for fifteen minutes fails as `host-lost`. A
reclaimed, cancelled, or completed job rejects an old report. A report with
the current token can still finish the job after five minutes if no reclaim or
host-lost transition has replaced it. Step logs require a live, unexpired
five-minute claim as well as its token. The host polls again
after `200 null`; it must not treat that response as a failure.

For each SSH step, the host logs against its exact ordered name
`deploy[index]:kind`, where `index` is zero-based. A log message must be
nonempty, at most 512 characters, and contain no control characters; the step
name must be at most 80 characters and belong to the claimed job. The log
endpoint writes each accepted message into the corresponding deployment run.
The report's `steps` is an ordered prefix of those same names. Only its last
entry may fail. `healthy: true` requires every step to appear and succeed.
The accepted evidence codes are `health-ok`, `health-http-failed`, and
`deploy-step-failed`; an unknown code is replaced with a bounded success or
failure code. The run shows each reported step result. A failed deploy can
queue the previous healthy artifact for rollback when no newer job is waiting
for that environment.

To resolve a schema 2 secret variable, the deploy host calls
`POST /api/runner/secrets/resolve` with a Runner bearer, `purpose: "deploy"`,
`reference: "secret://{project}/{environmentId}/{secretName}"`, and a
`workload` of `{ "kind": "deploy-job", "project": "...", "jobId": "...",
"leaseToken": "..." }`. The secret provider verifies the live job lease
and scope before returning the value. The value must not be copied into a
deploy log, report, or release snapshot. The old host-local `secretRefs` store
remains for legacy template jobs; schema 2 variables use the secret provider.

`agent-host deploy-poll <server-url> <host-id> <token-file> <artifact-store>
[secret-store]` makes one claim; `deploy-loop` repeats it every five seconds.
The host needs its artifact store, OpenSSH with pinned host keys, and access to
the secret provider route. The SSH target needs Python 3; container targets
also need Docker Compose. The host reports the health result after executing
the ordered steps. Operators read run state and logs through
`GET /api/projects/{project}/deploy/deployments/{deploymentId}` and
`GET .../log[?step=]`, without exposing the claim token.
