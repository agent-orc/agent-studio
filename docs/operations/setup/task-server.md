# Task Server deployment and recovery

Status: production bootstrap, topology release, and sole v1 ownership contract,
AGT-2192/AGT-2196/AGT-2330, 2026-07-25.

This runbook implements the Task Server boundary from
[Distributed Agent Studio target architecture](../../concepts/distributed-agent-studio-target-architecture.md).
The service is the durable task and orchestration authority. Agent Studio,
OrchestratorApi, and Agent Runner are clients. The Task Server is the only
owner of its SQLite store and `/api/v1`. Internet-reachable deployments require HTTPS,
authenticated mode, protected credentials, and the broader AGT-2193 controls
in [networked Task Server](networked-task-server.md).

## Package and process boundary

| Package | Runtime responsibility | Durable data |
|---|---|---|
| `contracts/TaskServer.Contracts` | Versioned resource, runner, review, event, artifact, management, and compatibility DTOs | None |
| `task-server` | Stable identities, tasks, runs, immutable review subjects, review attempts, reports, events, artifacts, audit, migrations, backup/restore, leases, fences, and management API | Its configured data directory only |
| `studio-bff` | Optional stateless same-origin proxy for Agent Studio | None |
| `runner` | Separately registered coding and review services, host probes, Git worktrees, CLI and review processes, bounded execution, and durable result delivery through protocol 2 | Host worktrees, fsynced outboxes, and bounded transfer state only |

The Task Server project references shared contracts and SQLite persistence. It
does not reference Angular, the legacy Studio backend, Agent Runner, coding
agent libraries, repository worktree code, or host process execution.

## Install and supervise

Publish each process independently:

```bash
dotnet publish task-server/TaskServer.csproj -p:PublishProfile=linux-x64 -o out/task-server
dotnet publish studio-bff/StudioBff.csproj -c Release -o out/studio-bff
dotnet publish runner/AgentRunner.csproj -c Release -o out/runner
```

The Task Server profile emits one self-contained `linux-x64` executable with
the SQLite native runtime embedded. It needs neither a repository checkout nor
a .NET installation and reads host-specific bootstrap values only from
`server.env`.

For Linux, install the release under the versioned
`/opt/agent-orchestrator/<version>/` directory and point
`/opt/agent-orchestrator/current` at it. Copy
[`agent-task-server.service`](../../../deploy/systemd/agent-task-server.service)
and the
[`backup service`](../../../deploy/systemd/agent-task-server-backup.service)
and [`timer`](../../../deploy/systemd/agent-task-server-backup.timer) to
`/etc/systemd/system/`. Create `/etc/agent-orchestrator/server.env` from
[`agent-task-server.env.example`](../../../deploy/systemd/agent-task-server.env.example).
The data directory must be owned by the dedicated service account and backed up
independently of the installation directory.

Create the bootstrap bearer file without putting the secret in shell history:

```bash
sudo install -d -m 0750 -o root -g agent-orchestrator /etc/agent-orchestrator
sudo sh -c 'umask 0077; read -r secret; printf "%s\n" "$secret" > /etc/agent-orchestrator/task-server.token'
sudo chown root:agent-orchestrator /etc/agent-orchestrator/task-server.token
sudo chmod 0640 /etc/agent-orchestrator/task-server.token
```

Use a randomly generated value of at least 32 characters and transfer client
copies through the host administration channel. Never put the value in a
command line, task, log, or committed file.

The service manager owns process start, stop, restart, and upgrade:

```bash
sudo systemctl enable --now agent-task-server
sudo systemctl enable --now agent-task-server-backup.timer
sudo systemctl status agent-task-server
sudo systemctl stop agent-task-server
sudo systemctl restart agent-task-server
```

Before an upgrade, put the server in `Draining`, wait for active attempts to
finish, create a backup, switch to `Maintenance`, stop the unit, replace the
published package, and start it again. Startup applies additive schema
migrations before `/readyz` reports that lease and fence authority is restored.

## Windows install and supervision

There is no systemd on Windows, so the Task Server runs as a non-interactive
Scheduled Task instead of an interactive session process. Publish with the
`win-x64` profile:

```powershell
dotnet publish task-server\TaskServer.csproj -p:PublishProfile=win-x64 -o out\task-server
```

This emits the same kind of self-contained single executable
(`task-server.exe`) as the Linux profile, with the SQLite native runtime
embedded. It needs neither a repository checkout nor a .NET installation on
the target host and reads its bootstrap values only from `server.env`.

Install the release under a versioned `C:\AgentOrchestrator\<version>\`
directory and point `C:\AgentOrchestrator\current` at it (a directory junction
via `mklink /J` is the closest analog to the Linux `current` symlink). Create
the host-owned bootstrap file at `C:\ProgramData\AgentOrchestrator\server.env`
using the same `KEY=VALUE` contract as
[`agent-task-server.env.example`](../../../deploy/systemd/agent-task-server.env.example),
including `LISTEN_URL`, `STORE_PATH`, `BACKUP_PATH`, `AUTH`, and
`AUTH_TOKEN_FILE`. Restrict it to the service account with `icacls`, and
generate the bearer token the same way as Linux: a randomly generated value of
at least 32 characters, transferred through the host administration channel
and never put in a command line, task, log, or committed file.

Register and supervise the process with the scripts in
[`deploy/windows/task-server/`](../../../deploy/windows/task-server/), run
from the Studio checkout:

```powershell
.\deploy\windows\task-server\register-task-server.ps1 `
    -InstallRoot C:\AgentOrchestrator\current `
    -EnvFile C:\ProgramData\AgentOrchestrator\server.env
```

For a versioned Stable release, the packaged install helper publishes the
matching SHA, creates or verifies the dedicated data and backup directories,
repoints the guarded `current` junction, writes `TaskServer:BaseUrl` to the
host-owned Stable configuration, and registers the Scheduled Task:

```powershell
.\deploy\windows\task-server\install-task-server-release.ps1 `
    -SourceCheckout C:\Projects\agent-taskboard-stable `
    -ReleaseSha <40-character-main-sha> `
    -DataDirectory C:\Projects\agent-taskboard-devspace\task-server-data `
    -StableConfigurationPath C:\Projects\agent-taskboard-stable\backend\appsettings.Local.json
```

Keep `DataDirectory` outside every versioned installation. `-WhatIf` previews
the host mutations. The helper refuses to replace a non-junction `current`
directory, rejects a data directory below the installation root, and refuses
to reuse a release directory whose binary does not report the requested SHA.
It reconciles `LISTEN_URL`, `STORE_PATH`, and `BACKUP_PATH` in an existing
`server.env` while preserving its authentication settings. The versioned
package includes the detached supervisor script, so the registered Scheduled
Task does not execute service code from a mutable Studio checkout. When the
task already exists, the helper waits for its process tree to stop before
repointing `current`; the rollout probe then requires the candidate Git SHA
from both direct and proxied management status before Stable can be unpinned.

`register-task-server.ps1` registers `AgentOrchestrator-TaskServer` as an
`AtStartup`-triggered Scheduled Task under an `S4U` principal - services never
run as session tasks bound to an interactive logon. Its action is
`start-task-server.ps1`, a detached supervisor that reads `server.env` into
the child process environment, launches `task-server.exe`, redirects its
stdout/stderr to timestamped files under
`%ProgramData%\AgentOrchestrator\task-server\`, and restarts it after
`RestartDelaySeconds` (default 5, the Windows analog of the unit's
`Restart=always` / `RestartSec=5s`) whenever it exits. The Scheduled Task's
own `RestartCount`/`RestartInterval` settings add a second layer of recovery
if the supervisor process itself is lost.

The Scheduled Task owns process start, stop, restart, and upgrade:

```powershell
Start-ScheduledTask -TaskName AgentOrchestrator-TaskServer
Get-ScheduledTask -TaskName AgentOrchestrator-TaskServer | Get-ScheduledTaskInfo
Stop-ScheduledTask -TaskName AgentOrchestrator-TaskServer
```

`Stop-ScheduledTask` terminates the process tree without a graceful drain
signal, so before an upgrade or a planned stop, put the server in `Draining`
through the management API, wait for active attempts to finish, create a
backup, switch to `Maintenance`, then stop the task, replace the published
package under the versioned directory, repoint `current`, and start the task
again - the same sequence as the systemd upgrade path above.

No packaged Windows backup timer ships yet. Invoke the same backup subcommand
manually or from an operator-created Scheduled Task with a `Daily` trigger
targeting the installed executable, mirroring
[`agent-task-server-backup.timer`](../../../deploy/systemd/agent-task-server-backup.timer):

```powershell
C:\AgentOrchestrator\current\task-server.exe backup --name manual
```

## Configuration and health

The production binary consumes one host-owned `server.env` bootstrap contract.
These values are process prerequisites and are not agent-editable operational
settings.

| Setting | Meaning | Default |
|---|---|---|
| `LISTEN_URL` | Kestrel addresses. `AUTH=none` is rejected unless every address is loopback. | `http://127.0.0.1:5071` |
| `STORE_PATH` | Private database and migration evidence root, outside every version directory | `data` beside the installed service |
| `BACKUP_PATH` | Verified SQLite backup destination | `<STORE_PATH>/backups` |
| `AUTH` | `bearer` in production; `none` is loopback-only | `none` |
| `AUTH_TOKEN_FILE` | Host-owned bearer secret file, minimum 32 characters | Required with `AUTH=bearer` unless `AUTH_TOKEN` is set |
| `AUTH_TOKEN` | Direct secret alternative, mainly for ephemeral deployments | Unset |
| `TaskServer:MinimumLeaseSeconds` | Lower clamp for Runner leases | `30` |
| `TaskServer:MaximumLeaseSeconds` | Upper clamp for Runner leases | `600` |
| `TaskServer:ResultFinalizationMaxAttempts` | Bounded application-owned summary attempts after CORE completion | `3` |
| `TaskServer:InvariantReconciliationSeconds` | Interval for Tranche 0 invariant comparison | `30` |
| `TaskServer:InventoryGraceSeconds` | Minimum age before inventory mismatches are actionable | `120` |
| `TaskServer:MaximumEventPayloadBytes` | Hard UTF-8 size limit for one typed event payload | `262144` |
| `TaskServer:RequireAuthentication` | Require distinct Studio and Runner bearer credentials on `/api/v1` | `false` |
| `TaskServer:StudioBearerToken` | Studio/BFF read and management credential | unset |
| `TaskServer:RunnerBearerToken` | Runner registration, claim, renew, event, artifact, and completion credential | unset |

- Configure exactly one of `AUTH_TOKEN_FILE` or `AUTH_TOKEN`.
- `GET /api/v1/protocol` and `POST /api/v1/protocol/compatibility` remain open
  so a client can negotiate before registration. All other v1 requests require
  the bearer credential when `AUTH=bearer`.
- `GET /healthz` proves the process is live.
- `GET /readyz` succeeds only after schema integrity and durable lease/fence
  authority are restored.
- `GET /api/v1/management/status` reports server identity, version, schema,
  data root, mode, and supported protocol range. In the local Studio
  compatibility profile, the loopback `local-default` operator can use the
  management plane without creating a human account. Networked Studio
  deployments require a signed-in owner or operator.
- `GET /api/v1/management/invariants` reports invariant definitions, recent
  violations, and pending idempotent runner actions.
- `GET /api/v1/protocol` publishes the compatibility range. Every versioned
  resource request must carry `X-Task-Protocol-Version`. An unsupported or
  missing version gets HTTP 426 with a structured reason before any mutation.
- `GET /api/v1/projects/{projectId}/tasks/{taskIdentity}/history?after={cursor}`
  is the canonical reconnect projection. It includes every run, cursor-ordered
  typed events after the requested cursor, artifacts, related audit records,
  the latest typed Result-finalization state, and the last returned cursor.
- `POST /api/v1/runs/{runId}/result-finalization` is the fenced, idempotent
  awaited post-core gate. The Runner repeats only this request while the server
  returns `Retryable`; `Ready` includes the generated `status.md` artifact hash,
  and bounded exhaustion returns terminal `Degraded` without reissuing CORE.

Every release answers `task-server --version` with the release and stamped Git
SHA. This output is also used by deployment verification:

```text
task-server <VERSION>+sha.<40-character-commit>
```

## Sole v1 owner and transition proxy

Only a valid absolute HTTP or HTTPS `TaskServer:BaseUrl` selects the standalone
Task Server. OrchestratorApi then maps `/api/v1` only as a transparent proxy to
that origin and does not map its local management v1 routes.
`TaskServer:AuthTokenFile` or `TaskServer:AuthToken` supplies the proxy's
service credential.

Without that remote URL, the interim monolith profile derives local mode from
its registry-backed watch paths. It keeps the local management routes and uses
the in-process Orchestrator Chat context store directly. No self-reference URL
or environment-specific override is required. A missing, blank, or unusable
remote URL cannot fail boot or a context-list request. Any AGT-2325
compatibility review routes belong only to that local profile. They must never
be mounted beside the standalone proxy.

The canonical production bootstrap uses one service credential through
`AUTH=bearer`. The interim compatibility profile may instead set
`TaskServer:RequireAuthentication` and distinct `StudioBearerToken` and
`RunnerBearerToken` values. Do not configure both modes. Studio BFF reads
`TaskServer:AuthTokenFile`, `TaskServer:AuthToken`, or the compatibility
`TaskServer:BearerToken`. Agent Runner reads its secret from
`RUNNER_AUTH_TOKEN_FILE` or `RUNNER_AUTH_TOKEN`. A private-CA or rehearsal
deployment may pin the Task Server leaf certificate by SHA-256 through
`TaskServer:TlsServerCertificateSha256` on the BFF and
`RUNNER_TLS_CERTIFICATE_SHA256` on the Runner. Public deployments should use
the operating-system trust store.

For a zero-argument local profile, set `TASK_SERVER_PROFILE=local-compatibility`.
The service listens on `127.0.0.1:5031` and uses the current user's application
data directory. The topology test separately proves the service with another
process and temporary data root.

## Modes and durable authority

- `Normal` admits work and accepts writes.
- `Draining` stops new claims while allowing current fenced attempts to finish.
- `ReadOnly` permits observation and backup but blocks mutations.
- `Maintenance` blocks mutations and is required for import and restore.

A Runner lease release closes that attempt and atomically returns a matching
`3-progress` task to `2-ready`. This is the normal dead-process recovery path;
the later claim mints a higher fence. A successful completion instead closes
the lease and moves the task to `4-auto-review`.

Mode changes use `PUT /api/v1/management/mode` with a reason. On restart, every
previously active coding lease becomes `process-unknown`; its task cannot be
claimed by another Coding Executor. An operator must submit positive containment proof to
`POST /api/v1/management/attempts/{runId}/resolve-unknown`. The next claim then
uses a higher fence. Lease expiry alone never proves that the previous process
stopped.

A previously leased Remote ReviewAttempt also becomes `process-unknown`, but it
is safely reclaimable by a Review Executor with a higher durable fence. Review
workspaces are disposable, carry no product write credential, and cannot publish
product changes. The old executor's renew, report, and cleanup deliveries are
then rejected as stale. An infrastructure-only report creates a new
ReviewAttempt for the same immutable subject and leaves the task in Auto Review.
It never creates a coding run or returns the task to Ready.
Draining rejects new review claims while allowing an already fenced attempt to
renew, report, and clean up. Safe-shutdown and restore checks count unresolved
coding and review authority, and the integrity digest inventories the review
subject, attempt, fence, and delivery tables.

## Fully remote review authority

`POST /api/v1/reviews/subjects` records one immutable subject after a fenced
coding completion has persisted the same repository identity and URL, full
Result-SHA, and immutable ref or source-bundle digest. The review policy is a command plan:
completion interpretation, build and tests, requirements, code quality,
documentation, evidence, artifacts, and optional vision remain the existing
review steps, but their processes run only on a claimed Remote Review Executor.

Review lifecycle routes:

- `POST /api/v1/runners/{id}/review-claims`
- `POST /api/v1/reviews/attempts/{id}/lease/renew`
- `POST /api/v1/reviews/attempts/{id}/report`
- `POST /api/v1/reviews/attempts/{id}/cleanup`

The fenced report binds repository identity, expected and actual HEAD, tree
hash, dirty-before and dirty-after facts, environment, executable-digest
toolchain identity, exact command arguments, exit or signal, output digests,
artifacts, and typed aspect verdicts.
The Task Server validates containment and subject identity but starts no Git,
build, test, provider CLI, semantic, or vision process. Product and pass
outcomes advance to Human Review, which remains the final decision surface.
`ReviewInfra` stays in Auto Review and schedules another ReviewAttempt on the
same subject. Coding and review capabilities require distinct registered
identities, and a registered identity cannot be switched between those roles.
A stale report is rejected if a newer task lifecycle or result has replaced its
immutable review subject.

After draining, `POST /api/v1/management/prepare-shutdown` verifies that no
`active` or `process-unknown` attempt authority remains, records the operator
reason, and enters `Maintenance`. A safe response is permission for the service
manager to stop the process; the API does not try to stop its own host process.

## Backup and restore rehearsal

`POST /api/v1/management/backups` creates a consistent SQLite backup, runs an
integrity check, and returns its SHA-256. Backups contain server/workspace/
project/task/run identities, task state, events, artifact content, audit,
Runner records, coding and review leases, immutable review subjects, fenced
reports, and fence counters.

The packaged timer calls the same implementation through the binary:

```bash
/opt/agent-orchestrator/current/task-server backup --name timer
```

The command reads the same `server.env`, applies schema migrations
idempotently, takes and verifies the snapshot, writes the audit record, prints
the backup result as JSON, and exits. It does not turn live leases into
`process-unknown`; taking a backup is not a server restart.

Verify a backup without changing data:

```json
POST /api/v1/management/restore
{"backupId":"<id>","verifyOnly":true}
```

For restore, drain and resolve all attempts, enter `Maintenance`, then repeat
with `verifyOnly:false`. Restore refuses unresolved `active` or
`process-unknown` authority. Before replacement it creates a private safety copy
of the live store. It verifies schema compatibility and integrity after
replacement, automatically rolls back to that safety copy on failure, and
remains in `Maintenance` until an operator explicitly resumes normal service.

## Legacy single-writer migration

Legacy absolute paths and `watchPath` are migration inputs only. They never
become resource identity.

1. Call `POST /api/v1/management/migrations/legacy/inventory` with the legacy
   root and workspace name. Save the project/task/event/artifact counts,
   warnings, evidence-Git roots, and migration ID.
2. Stop every legacy writer. Confirm Studio task mutations and the in-process
   runner are stopped. A delta replay is acceptable only if it ends with the
   same exclusive writer freeze.
3. Put Task Server in `Maintenance` and call the matching `/import` route with
   `freezeConfirmed:true` and `expectedMigrationId` set to the saved inventory
   ID. Import fails if task metadata, prompts, timelines, or result artifacts
   changed after inventory.
4. The server creates a pre-import backup, imports the inventory in one
   transaction, preserves task `results/`, timeline events, stable generated
   identities, and copies evidence Git metadata into
   `migration-evidence/{migrationId}`.
5. Compare counts and save the returned integrity SHA-256. Start Task Server as
   the only writer, then point Studio/BFF and Runner at its URL.
6. The rollback boundary is the returned pre-import backup plus the untouched,
   frozen legacy root. Roll back before allowing either side to accept another
   write. After cutover, never reactivate the legacy writer against the same
   logical tasks.

The automated acceptance suite rehearses inventory, freeze enforcement,
transactional import, integrity verification, backup/restore, evidence Git
preservation, restart fencing, protocol rejection, and separate process
lifecycle.

### Planned local Windows cutover

Treat a move from the OrchestratorApi-owned v1 routes to the standalone service
as a release hold until all of these steps have durable evidence:

1. Copy the complete legacy `TaskRepository`, including `identities/`,
   `.metadata/attempt-authority.json`, and every
   `.metadata/attempt-authority.archive-*.json`, to a rehearsal root while Stable is
   still on the held release. Inventory and import that copy into an empty
   rehearsal Task Server store. The inventory and import counts must agree for
   runner identities, tasks, coding attempts, review attempts, and leases, and the returned
   authority epoch and integrity SHA-256 must be recorded. Any missing or
   unreadable live or archived attempt-authority store aborts the cutover. Imported live leases
   become `process-unknown`; they are never made claimable merely because the
   owner process was stopped.

   Set `requireAttemptAuthority:true` on both migration requests. This converts
   a missing authority file from an inventory warning into the blocking
   `legacy-attempt-authority-required` conflict used by this cutover.
2. Freeze the real legacy writer, repeat inventory against the real root, enter
   Task Server `Maintenance`, and import with the exact migration ID. Retain
   the untouched frozen root and the returned pre-import backup until the
   rollout is accepted. Resolve every `process-unknown` coding authority only
   with positive containment proof. Review authority can follow the fenced
   review reclaim contract.
3. Set Stable's gitignored `backend/appsettings.Local.json`
   `TaskServer:BaseUrl` to `http://127.0.0.1:5071`. Configure the matching proxy
   credential when bearer authentication is enabled. Do not restart
   OrchestratorApi until Task Server `/readyz` and the management status route
   are green.
4. Install two host-owned shell wrappers beside the dev and Stable checkouts.
   `deploy-task-server.sh <stable-checkout> <target-sha>` invokes
   `install-task-server-release.ps1`; `start-task-server.sh` invokes
   `Start-ScheduledTask`. Point `ATP_TASK_SERVER_DEPLOY_SCRIPT` and
   `ATP_TASK_SERVER_START_SCRIPT` at them when their paths differ from the
   devspace defaults. The versioned updater stops Stable, installs and starts
   Task Server, proves direct readiness, starts the API, then proves the proxy
   and browser boot. A detached held checkout stays detached at the candidate
   SHA until every cutover probe passes, then it is attached to `main`. A failed
   candidate remains detached for operator recovery.
5. Exercise one fenced coding claim through completion, one immutable review
   claim through report and cleanup, result-finalization and report artifact
   submission, and the board projection for the same task. Save request IDs,
   task and attempt IDs, fences, response statuses, management status, the
   deployed SHA, Scheduled Task status, and the final attached Stable branch in
   the task's collected `results/` directory.

The release hold is cleared only after step 5. A successful process health
check without the claim, review, report, and board round trip is not cutover
evidence.

## Release topology rehearsal

The release-blocking harness is intentionally separate from browser E2E. Build
the deployables once, then run the topology and compatibility gate:

```bash
dotnet build agent-taskboard.sln
dotnet test runner.Tests/AgentRunner.Tests.csproj \
  --no-build \
  --filter "FullyQualifiedName~AgentRunner.Tests.LogShipperCapTests|FullyQualifiedName~AgentRunner.Tests.BoundedOutputBufferTests"
dotnet test task-server.Tests/TaskServer.Tests.csproj \
  --no-build \
  --filter "FullyQualifiedName~TaskServer.Tests.TopologyTests|FullyQualifiedName~TaskServer.Tests.ProtocolTests" \
  --logger "console;verbosity=normal"
```

The test owns only its exact child PIDs and temporary directories. It never
sweeps by process name. Its parent-PID assertions require Task Server, Studio
BFF, and Runner to be siblings owned by the harness, so stopping Studio cannot
implicitly stop either service.

Deployment cards and releases also run the shared
[deployment regression scenario](../testing/deployment-scenario.md). Use its
`inproc smoke` target for a fast host-native gate, `compose full` for release
topology, and `remote full` for control-plane or cutover evidence.
