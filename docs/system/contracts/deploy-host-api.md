# Deploy host API contract

The deploy host polls Studio for work created from a release's committed
`.agent-studio/project.yml` schema 2 definition. The release stores the effective
environment declarations and the ordered SSH deploy steps. Existing
`docs/deployments/environments/*.json` files are checked for divergence;
schema 2 supplies effective environments and SSH targets when they are absent.
Release creation refuses a declaration that diverges from schema 2. See the
[project deployment definition](../domains/pipeline.md#project-deployment-definition-p1)
for migration and validation.

The API uses JSON with camel-case property names. On a networked installation,
the deploy host routes require HTTPS and a Runner service bearer with `runner.deploy`.
The claim request's `hostId` must equal the enrolled Runner principal's ID.
The job report and step-log routes additionally require the current claim's `leaseToken`;
the token is a claim capability and must stay out of operator logs and UI
responses. The public demo denies these execution routes.

| Method and path | Request | Response |
|---|---|---|
| `POST /api/deploy/jobs/claim` | `{ "hostId": "runner_deploy_1" }` | `200` with one `DeployJob`, or `200` with `null` when no job is claimable. A networked caller with a different enrolled host ID gets `403`. |
| `POST /api/deploy/jobs/{project}/{jobId}/log` | `{ "leaseToken": "...", "step": "deploy[0]:static-site", "message": "..." }` | `200` after appending the line to the deploy run; `409` if validation or the claim fails. |
| `POST /api/deploy/jobs/{project}/{jobId}/renew` | `{ "leaseToken": "..." }` | `200` after extending a live claim by five minutes; `409` for an expired or fenced token. |
| `POST /api/deploy/jobs/{project}/{jobId}/report` | `{ "leaseToken": "...", "healthy": true, "evidence": "health-ok", "steps": [{ "name": "deploy[0]:static-site", "succeeded": true }] }` | `200` after closing the job and reconciling the run; `409` for a stale token, invalid step sequence, or a job that is no longer claimed. |
| `GET /api/deploy/hosts/{hostId}/containers` | No body. The caller's enrolled Runner ID must equal `hostId`. | Last successful container template targets on this host, including deployments made before host observation was added. |
| `POST /api/deploy/hosts/{hostId}/observed-state` | Bounded per-environment Docker state, restart count, crash events and masked crash output. | `200` with the number of accepted observations. The caller's enrolled Runner ID must equal `hostId`. |

When a poll has no deploy job, the host inspects its listed containers and reads
the last 20 Docker log lines from stdout and stderr. It masks values found in
the container environment and common credential assignments before reporting;
Studio enforces a 20-line, 400-character-per-line bound and masks credential
assignments again. An observed crash loop opens or updates a durable environment
runtime incident and raises an Activity alert. A later running observation
closes the incident and records recovery. The project Operations view shows the
host restart count and last crash output even without application telemetry.

## Run host identity and liveness

An Owner issues a run host identity from Settings > Access > Runner
enrollments or Hosts > Add host, both with the identity "Run host (deploy)".
The enrollment code carries only `runner.deploy` and the projects the Owner
chooses; the claim route skips jobs of other projects, so a code without a
project deploys nothing. The page prints the host commands: `agent-host
--enroll` saves the runner id (`runner_...`) in `installation.env`, the second
command copies it into `/etc/agent-runner/deploy.env` as `RUNNER_HOSTNAME`,
and `agent-runner-deploy.service` claims with that id as its `hostId`. The
same id, or the host's unique enrollment name, is the `targetHostId` of every
environment the host serves. The Task
Server host join token stays coding and review only: it mints a Task Server
principal, which these routes do not accept.

A run host sends no claim heartbeat. Its contact is its claims, renewals,
reports and run-target polls: each one stamps the identity's `lastSeenAt`, a
claim that returns a job also `lastClaimAt`, and a report `lastReportAt`.
`GET /api/deploy/run-hosts` lists every run host with a `state` of
`reachable` (contact within two minutes), `unreachable`, or `no-contact`;
Hosts shows each one as a "Run host" role row. `GET
/api/projects/{project}/environments` adds `runHosts`, the status of each
declared `targetHostId` under that id, resolving an enrollment name to its run
host, with `not-enrolled` for an id no run host holds, and
the Deploy area names that host and its state on each environment card.

The claim response supplies `id`, `project`, `environmentId`, `hostId`,
`artifact`, `template`, `destination`, `healthUrl`, `secretRefs`, `containerPort`,
`previousArtifact`, `status`, `leaseToken`, timestamps, and claim metadata.
It also carries a nullable `tenant`. Released jobs carry selected plain values
in `resolvedVariables` and a `secretVariableRefs` map from workload variable
name to provider entry name; secret values do not appear in the claim. Schema 2
jobs carry `sshSteps` and `artifacts` from the release snapshot. Each `sshSteps`
item contains its `step` (`static-site` or
`container`), `artifact`, `target`, destination (`directory` or `composeFile`
and `service`), `healthUrl`, `healthTimeoutSeconds`, and environment scope.
Non-release jobs may still carry `variables`; each one has a `name` and either
a plain `value` or a secret name in `secret`. The deploy host uses the snapshot,
not the repository's current working tree, to execute the
job. A claim for a release's environment goes to its currently registered host.
The SSH executor requires each step's `target` to equal that host. The
backend writes the routed host into every step's `target` when it queues the
job, so after a host change a rollback or redeploy of an older release runs on
the current host with the snapshot's artifact, paths and variables. Release
verification compares the snapshot steps with that same host substitution.
Schema 2 jobs use the `ssh-v2` template so an older deploy host refuses them
instead of running its local Docker or static template. Deploy the backend
before installing the new agent-host binary on deploy hosts. Then switch
projects to schema 2. The backend supports the old host's `/renew` 404
compatibility path during this rollout.
Artifact provenance still names the underlying `container` or `static` kind.
All SSH steps in one release must use the same artifact kind.

A tenant deployment requires a `tenantTargets[tenant]` entry in the release's
committed environment declaration. Its `destination` and `healthUrl` must be
distinct from the project target and every other tenant target. A missing or
colliding target is refused before queueing. The host uses a distinct container
name for a tenant template job. For SSH steps, it uses a tenant directory under
the declared static directory or compose file parent. A container SSH step
also supplies a stable tenant-specific Docker Compose project name. The host
checks that tenant's health URL and reports the result on that tenant's run.
Before a host claims the job, release verification checks the routed target,
SSH steps, plain variables and secret variable-name mappings against the
release snapshot for that tenant.
Tenant routing on an edge-hosted declaration is refused until the run target
supports an isolated tenant target.

One host receives one job at a time. A claim is live for five minutes; the host
renews it every minute while executing. A `409` renewal means the lease was
lost and cancels execution. The host retries network errors and other HTTP
failures for up to four minutes after its last successful renewal. A `404`
from an older backend means renewal is unsupported and allows that job to
finish. After expiry the same host can reclaim the job and receives a new
lease token. A job fails as `host-lost` after three claims without a report,
or after fifteen minutes without a report or reclaim. A
reclaimed, cancelled, or completed job rejects an old report. Schema 2 reports,
renewals, and step logs require a live, unexpired five-minute claim as well as
its token. A schema 1 host can report after five minutes without renewal only
while its original token has not been reclaimed and the job has not reached
`host-lost`. The host polls again
after `200 null`; it must not treat that response as a failure.
Legacy template health polling ends after 60 seconds and reports a health
failure if the URL has not recovered, even while the claim remains renewable.

For each SSH step, the host logs against its exact ordered name
`deploy[index]:kind`, where `index` is zero-based. A log message must be
nonempty, at most 512 characters, and contain no control characters; the step
name must be at most 80 characters and belong to the claimed job. The log
endpoint writes each accepted message into the corresponding deployment run.
It accepts at most thirty messages per job per minute. Failed SSH and SCP
commands contribute a bounded stderr tail with resolved variable values masked.
The report's `steps` is an ordered prefix of those same names. Only its last
entry may fail. `healthy: true` requires every step to appear and succeed.
The accepted evidence codes are `health-ok`, `health-http-failed`, and
`deploy-step-failed`; an unknown code is replaced with a bounded success or
failure code. The run shows each reported step result. A failed deploy can
queue the previous healthy artifact for rollback when no newer job is waiting
for that environment.

To resolve a schema 2 secret variable or a `secretVariableRefs` mapping, the deploy host calls
`POST /api/runner/secrets/resolve` with a Runner bearer, `purpose: "deploy"`,
`reference: "secret://{project}/{environmentId}/{secretName}"`, and a
`workload` of `{ "kind": "deploy-job", "project": "...", "jobId": "...",
"leaseToken": "..." }`. The secret provider verifies the live job lease
and scope before returning the value. The value must not be copied into a
deploy log, report, or release snapshot. The old host-local `secretRefs` store
remains for legacy template jobs; schema 2 SSH steps use the secret provider.
The workload receives the mapped variable name even when the provider entry
has a different name.

`agent-host deploy-poll <server-url> <host-id> <token-file> <artifact-store>
[secret-store]` makes one claim; `deploy-loop` repeats it every five seconds.
The host needs its artifact store, OpenSSH with pinned host keys, and access to
the secret provider route. The SSH target needs Python 3; container targets
also need Docker Compose. The host checks each step's health URL before starting
the next step, using its `healthTimeoutSeconds`, and reports a failed step when
health does not pass. Failed static copies remove remote staging before a retry.
The local SSH step limit is five minutes, longer than both remote compose
timeouts plus margin. A remote lock serializes each compose service or static
site switch. The remote helper starts Docker with a minimal environment and
refuses deploy variables that control Docker, Compose, dynamic loading, or
the executable search path. Static switches retain the latest three releases
and remove old temporary entries.
Operators read run state and logs through
`GET /api/projects/{project}/deploy/deployments/{deploymentId}` and
`GET .../log[?step=]`, without exposing the claim token.
