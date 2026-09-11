# Task Server deployment and recovery

Status: production bootstrap, scoped service principals, topology release,
sole v1 ownership contract, per-service release container images, and the
S3-compatible cold archive target, AGT-2192/AGT-2196/AGT-2330/AGT-2730/
AGT-2729/AGT-2746, 2026-09-11.

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
| `task-server` | Stable identities, tasks, runs, immutable review subjects, review attempts, reports, events, artifacts, audit, migrations, backup/restore, full backup sets, retention policy and scheduled archive sweeps, leases, fences, and management API | Its configured data directory only |
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

Create distinct Studio and Engine bootstrap files without putting either
secret in shell history:

```bash
sudo install -d -m 0750 -o root -g agent-orchestrator /etc/agent-orchestrator
sudo sh -c 'umask 0077; read -r secret; printf "%s\n" "$secret" > /etc/agent-orchestrator/studio.token'
sudo sh -c 'umask 0077; read -r secret; printf "%s\n" "$secret" > /etc/agent-orchestrator/engine.token'
sudo chown root:agent-orchestrator /etc/agent-orchestrator/studio.token /etc/agent-orchestrator/engine.token
sudo chmod 0640 /etc/agent-orchestrator/studio.token /etc/agent-orchestrator/engine.token
```

Use independently generated values with at least 256 bits of entropy. On an
empty authenticated store, omitted bootstrap values cause Task Server to
create the initial Studio and Engine credentials and print each secret once.
Capture those lines through the host administration channel. The server stores
only SHA-256 hashes and never returns a credential again after its create or
rotate response.

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
including `LISTEN_URL`, `STORE_PATH`, `BACKUP_PATH`, `AUTH`,
`STUDIO_AUTH_TOKEN_FILE`, and `ENGINE_AUTH_TOKEN_FILE`. Restrict both token
files to the service account with `icacls`, generate each independently with at
least 256 bits of entropy, and never put either value in a command line, task,
log, or committed file.

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

### Windows fallback profile (Orchestrator Engine, Studio connector, warm standby)

[`deploy/windows/orchestrator-engine/`](../../../deploy/windows/orchestrator-engine/)
and
[`deploy/windows/studio-connector/`](../../../deploy/windows/studio-connector/)
add the same register/start Scheduled Task pair for
`orchestrator-engine.exe` and the loopback Studio connector
(`agent-studio-bff.exe`) that `deploy/windows/task-server/` already provides
for the Task Server. `deploy/windows/fallback/` packages all three into one
installable profile plus the warm-standby backup pull and the switch drill
scripts described in the
[Windows fallback runbook](windows-fallback-runbook.md); that page is the
operator-facing entry point for Phase B slice B4
([Remote Task Server with local Agent Studio](../remote-task-server-local-studio.md)).
It is not a second production deployment path: the fallback profile installs
the same released binaries under the same Scheduled Task and `current`
junction conventions as this section, rested in `Maintenance` until a drill
or a real switch.

## Retention CLI

The Task Server binary owns the retention command surface for the legacy file
tree and, after the Phase B cutover, the server store. The legacy adapter reads
`projects/<project>/tasks/<bucket>/<task>/`, taking the lane and terminal time
from `task.json`, plus `.metadata/` and `logs/bus/`. Its
default cold target is the `agent-taskboard-archive` directory beside the
workspace, never a directory inside the Git repository.

```bash
dotnet task-server.dll retention plan --workspace /srv/agent-taskboard-workspace --policy default
dotnet task-server.dll retention apply --workspace /srv/agent-taskboard-workspace --policy retention-policy.json --json
dotnet task-server.dll retention restore --workspace /srv/agent-taskboard-workspace --policy default --task AGT-2743
dotnet task-server.dll retention re-excerpt --workspace /srv/agent-taskboard-workspace
dotnet task-server.dll retention --help
```

Use `--archive <path>` to override the sibling cold-store path. `--project`
and `--task` narrow a plan or apply run; a scoped run excludes the
workspace-wide `_workspace/_runtime` pseudo task. `plan` never moves or deletes
an artifact. It writes a versioned report under
`.metadata/retention-runs/<timestamp>-plan.json`. `apply` writes the same
before/after metrics, appends `.metadata/retention-audit.jsonl`, and records
each project's hot-tree deletions in one commit. The report groups action
counts and bytes by rule and project and lists the largest affected tasks.

`before` and `after` report `hotTaskBytes` without the excerpts and carry the
excerpt cost as its own `excerptBytes` figure, so an archive run does not read
as growth. Runtime rotation is committed separately: only tracked paths are
staged, untracked and ignored ones are skipped, and a rotation problem is a
warning that leaves the exit code at 0 rather than losing the archive commit.

`re-excerpt` rebuilds the hot excerpts from the cold payloads and stages them.
Use it after an excerpt-writer change, when the originals have already left the
hot tree. Excerpts are bounded: at most 20 error windows, a summarised timestamp
section (first, last, duration, and gaps over a minute), 100 commands, and
256 KB in total with a truncation marker.

The built-in policy keeps authority data hot, retains active and review lanes,
creates content-aware Markdown excerpts when heavy class-C originals become
cold after 30 terminal days, and leaves `task.json`, `status.md`, excerpts, and
the archive pointer hot after the 180-day task transition. Runtime bus logs are
deleted after 30 days and attempt-authority daily archives after 90 days.
Individual class-C files over 50 MiB are refused by every workspace evidence
commit path and reported with their relative path.

Full file-tree backup commands create a closed, externally copyable set:

```bash
dotnet task-server.dll retention backup-full --workspace /srv/agent-taskboard-workspace --out /srv/backups
dotnet task-server.dll retention verify-full --out /srv/backups/full/20260907T010203000Z
dotnet task-server.dll retention restore-full --workspace /srv/empty-restored-workspace --out /srv/backups/full/20260907T010203000Z
```

`backup-full` writes `workspace.bundle`, untracked evidence, referenced cold
manifests and payloads, then `inventory.json`, and writes `complete.json` last.
`inventory.json` schema version 1 contains `createdAt`, `workspaceName`,
`taskCount`, `coldPayloadCount`, `totalBytes`, a sorted `files[]` list
(`relativePath`, `size`, `sha256`), and `setSha256`, the SHA-256 of the stable
path/size/hash sequence, plus `warnings[]` and per-step `steps[]` timings
(`bundle`, `evidence-copy`, `manifests`, `hashing`). Archive pointers are read
only where they sit next to a `task.json`, so foreign copies a task carries
under `results/` or `attachments/` are ignored; pointers are validated before
the bundle step, and an unreadable one is a warning rather than an abort.
Verification checks every file and both set hashes.
Restore refuses a non-empty destination, clones the bundle, overlays untracked
evidence, restores the sibling cold tree, and rewrites archive pointers to that
new cold location.

## Retention against the SQLite store

After the Phase B cutover the same classifier, planner, and executor
(`retention/`) run against the server's own tables through
`SqliteRetentionStore` instead of the file tree. Class A rows (`tasks`,
`events`, `leases`, `audit`, `review_attempts`) are never enumerated by the
adapter and are never pruned or moved to cold storage. Coordination only
updates the task archive marker and appends audit history. Class B and C data
is the `artifacts` table: each row's `name` (for example
`logs/cli-output.log`, `status.md`) is classified exactly like a file-tree
path. `retention_policies` holds the workspace defaults and per-project
overrides as JSON rule sets; `archive_manifests` holds one row per task with
every archive stage (payload path, SHA-256, and per-file hashes) so a restore
can replay them in order; `archive_runs` holds one row per plan or apply with
its full report plus action counts and bytes grouped by rule.
`tasks.archive_state` and `archived_at` are set only once a whole task reaches
stage 2, and both are included in `TaskDto`, so a task list does not need to join
`archive_manifests` to show a stub.

The default stages are: stage 1 after 30 terminal days moves class C originals
to cold storage and keeps a class B excerpt hot; stage 2 after 180 terminal
days moves the remaining class B and C payload while keeping `status.md`, the
excerpt, and the manifest pointer hot; stage 3 after 730 terminal days can
delete cold payloads while retaining a tombstone manifest, but is disabled by
default. Stage 3 requires both `deleteArchiveEnabled: true` in the workspace
policy and `confirmColdDelete: true` on that apply request. Projects cannot
weaken the built-in never-archive lanes from Backlog through Human Review and
Escalated.

The workspace policy also carries `archiveStorage`
(`ArchiveStoragePolicy`). `archiveTarget` is `local` or `s3` (default
`local`). `copyToSecondary: true` is valid only with a local primary target:
it writes the local object first, then uploads it to S3 and verifies the
SHA-256 there before the run counts as complete.
`deleteLocalAfterVerification: true` requires `copyToSecondary` and removes
the local object once the S3 copy verifies. `deleteArchivedAfterYears` (1 to
100, unset by default, meaning never) drives stage 3 in place of the per-rule
`deleteArchiveAfterDaysTerminal` once set. Every archive stage records one
`ArchiveObjectReference` per uploaded object (target name, object key, ETag,
SHA-256, size, and the target's own server-side checksum when it returns one)
in `ArchiveManifest.Objects`, keyed
`<project>/<task-key>/<archivedAt:yyyyMMddTHHmmssfffZ>/<file>` under an
optional bucket prefix. `LocalDirectoryTarget` and `S3Target`
(`retention/ArchiveTargets.cs`) both implement the same `IArchiveTarget`
contract, so the planner, restore, and the weekly integrity check below never
special-case which target holds a payload.

Restore tries every target recorded on a stage's objects until one verifies
the payload's SHA-256 (and, for a ZIP payload, every entry hash) before
writing content back to SQLite; a full backup restored onto a host with the
same S3 target configured resolves archived artifacts through the recorded
object references without a separate cold-payload copy (see "Full backup
sets" below).

Archiving a heavy file clears `artifacts.content` to `NULL` and sets
`archived = 1`; the row keeps its `sha256` and `size_bytes`. A content read on
an archived artifact,
`GET /api/v1/runs/{runId}/artifacts/{artifactId}/content`, returns HTTP 409
`artifact-archived` with the archived task's id, key, and manifest URL instead
of an empty body. Restoring rewrites the content back and re-verifies every
file's hash before clearing `archived`.

Management routes, all under `/api/v1/management/retention`:

| Route | Purpose | Scope |
|---|---|---|
| `GET`/`PUT /policy` | Read or version-guarded write of the workspace policy (all four artifact classes required on write) | `tasks:read` / `management` |
| `GET`/`PUT`/`DELETE /policy/projects/{projectId}` | Read, write, or reset one project's rule overrides (partial rule sets allowed) | `tasks:read` / `management` |
| `POST /plan` | Dry run; returns the plan and records an `archive_runs` row in mode `plan` | `management` |
| `POST /apply` | Runs now; skips tasks with an active or process-unknown lease; refused with `server-not-writable` in `ReadOnly` and `Maintenance`; cold deletion additionally requires `confirmColdDelete: true` | `management` |
| `GET /runs`, `GET /runs/{id}` | Run history with the full report | `tasks:read` |
| `GET /target` | Active archive policy and non-secret local/S3 target configuration status | `tasks:read` |
| `POST /integrity/check` | Sample manifests and re-verify every recorded object and ZIP entry hash; optional body `{"sampleCount": 25}` | `management` |
| `GET /archive/{taskId}` | Manifest for an archived task (accepts a task id or task key) | `tasks:read` |
| `POST /archive/{taskId}` | Archive one task now, bypassing age thresholds; body is `{"stage":1|2|3,"confirmColdDelete":false}`; active leases and policy-protected lanes are refused | `management` |
| `POST /archive/{taskId}/restore` | Restore, with every file hash re-verified | `management` |

`RetentionSchedulerHostedService` mirrors `ResultRefGcHostedService`: a
`PeriodicTimer` checks once per configured hour (default 03:00 server-local
time, `TaskServer:RetentionScheduleHour`) and runs at most once per local day.
It skips
while the server is `Draining`, `ReadOnly`, or `Maintenance`, or while
`AuthorityReady` is false, or when the one-minute host load per core exceeds
`TaskServer:RetentionMaximumLoadPerCore`. A scheduled run records
`trigger=scheduled`, appends a distinct `retention.run.completed` audit action,
and publishes the same event over `/hubs/events` as `taskServerEvent` for the
Studio feed. It then creates a full backup set and thins complete sets to the
policy union of 7 daily, 4 weekly, and 12 monthly sets. An archive, event, or
backup failure is logged and never stops the server.

On the configured weekly day (`TaskServer:RetentionIntegrityDayOfWeek`,
default `Sunday`) the same scheduler also runs an integrity sample of up to
`TaskServer:RetentionIntegritySampleCount` (default `25`) archive manifests:
it re-verifies every recorded object against its target and, for anything
that verifies, re-hashes every ZIP entry against the manifest. The run is
stored in `archive_runs` with `mode=integrity`; a discrepancy appends a
`retention.integrity-discrepancy` audit record scoped to that run and
publishes `retention.integrity.discrepancy` (or `retention.integrity.clean`
when nothing is wrong) over the same Studio feed hub.

The legacy migration import
(`POST /api/v1/management/migrations/legacy/import`) runs the active policy
against the freshly imported inventory before returning, so heavy data already
past the archive threshold lands directly in the cold archive instead of
sitting hot; `LegacyMigrationResult.ArchivedTasks` and `.ArchivedBytes` report
what moved.

The retention CLI gains a `--store <STORE_PATH>` option for `plan`, `apply`,
and `restore`, pointing at a Task Server data directory instead of a workspace
checkout. It opens the store the same way `backup` does (authority restored
without the process-unknown restart quarantine, since a retention pass over an
offline copy must not mutate lease or fence state):

```bash
dotnet task-server.dll retention plan --store /srv/agent-orchestrator/data --json
dotnet task-server.dll retention apply --store /srv/agent-orchestrator/data --project AGT --json
dotnet task-server.dll retention apply --store /srv/agent-orchestrator/data --confirm-cold-delete --json
dotnet task-server.dll retention restore --store /srv/agent-orchestrator/data --task AGT-2744
```

## Full backup sets

`POST /api/v1/management/backups/full` builds a self-contained set under
`BACKUP_PATH/full/<backup-id>/`: the same verified SQLite snapshot
`POST /api/v1/management/backups` produces (`snapshot.db`), an explicit JSON
file for every archive manifest (`manifests/`), every referenced cold archive
payload (`cold/<project>/<task>/<timestamp>/payload.zip`), an analysis export
(`export/`), `inventory.json` (schema version 1: `files[]` with
`relativePath`/`size`/`sha256`, `taskCount`, `coldPayloadCount`, `totalBytes`,
`setSha256`, and `warnings[]`), and `complete.json` written last. The snapshot
is the source of truth for manifest enumeration and exports. A missing payload
or hash mismatch aborts the set before `complete.json` is written.
`GET /backups/full` lists sets from
their `inventory.json`/`complete.json` pair; `POST /backups/full/{id}/verify`
recomputes every file hash and checks the inventory and completion hashes;
`POST /backups/full/{id}/restore` requires
`Maintenance` mode, restores the snapshot through the same path as
`POST /management/restore`, atomically replaces the configured archive tree,
and rewrites every manifest payload path for that host. A pre-restore database
snapshot and the old archive tree form the rollback boundary if any later step
fails. A full set is therefore relocatable to a different `ARCHIVE_PATH`.
The ordinary SQLite backup and restore routes deliberately include only the
database. They do not copy cold payloads; use a full backup set whenever
archived artifacts must be recoverable from that backup alone.

When an S3 archive target is configured (see "Retention against the SQLite
store" above), `POST /backups/full` uploads the complete set under
`<prefix>/full/<backup-id>/` once the local `complete.json` exists,
uploading `complete.json` itself last. The summary and `GET /backups/full`
report `remoteState`: `not-configured`, `verified`, `hash-mismatch`,
`missing`, or `unavailable`, computed by re-reading the remote
`inventory.json`/`complete.json` pair and comparing `setSha256`. Thinning to
the 7 daily / 4 weekly / 12 monthly union deletes both the local and the
remote copy of every set outside the kept union. Stage 3 cold-payload
deletion (`confirmColdDelete: true`) is refused with
`archive-referenced-by-backup` for any payload a retained complete backup set
still references, so a set never silently loses a payload it depends on. A
deleted payload keeps its manifest as a tombstone and an audit record; the
"Cold archive" card on the Task Server settings page previews the affected
payloads and requires typing `DELETE ARCHIVED PAYLOADS` before sending the
confirmed apply.

The analysis export is versioned JSONL, one object per line:

| File | Schema version 1 fields |
|---|---|
| `tasks.jsonl` | Task id and key, project id/name, lane, archive state, created and updated timestamps |
| `runs.jsonl` | Run and task/project ids, status, runner, timestamps, calculated duration, task-context model list, input/output tokens, and `tokenAttribution: task-context` |
| `reviews.jsonl` | Attempt, subject, task, and project ids, status, verdict, created and reported timestamps |
| `integrations.jsonl` | Result-handoff repository id/URL, base and result SHA, immutable ref, acknowledgement and retention timestamps |
| `project-costs.jsonl` | Project token totals, `estimatedCostUsd: null`, and `pricingStatus: not-recorded-by-task-server-v1` until a durable pricing ledger exists |

The set is designed for DuckDB or notebook analysis without a running Task
Server. Run model/token attribution is explicitly task-context scoped because
the current schema has no durable run-to-model-usage relation.

CLI:

```bash
dotnet task-server.dll backup full --TaskServer:DataDirectory /srv/agent-orchestrator/data
dotnet task-server.dll backup verify-full <backup-id> --TaskServer:DataDirectory /srv/agent-orchestrator/data
dotnet task-server.dll backup restore-full <backup-id> --TaskServer:DataDirectory /srv/agent-orchestrator/data
```

## Container images

Every release tag publishes one container image per service to
`ghcr.io/agent-orc/`, built from the Dockerfile of the same name
(`backend/Dockerfile`, `frontend/Dockerfile`, `task-server/Dockerfile`,
`orchestrator-engine/Dockerfile`, `studio-bff/Dockerfile`,
`runner/Dockerfile`):

| Image | Dockerfile | Service |
|---|---|---|
| `agent-studio-api` | `backend/Dockerfile` | `OrchestratorApi` (board API, local orchestration) |
| `agent-studio-web` | `frontend/Dockerfile` | Production Angular bundle behind Caddy |
| `agent-task-server` | `task-server/Dockerfile` | Standalone Task Server |
| `agent-orchestrator-engine` | `orchestrator-engine/Dockerfile` | API-only flow execution service |
| `agent-studio-bff` | `studio-bff/Dockerfile` | Studio reverse proxy in front of the Task Server |
| `agent-host` | `runner/Dockerfile` | Agent Runner, coding CLIs baked in |

Each image is tagged three times: `v<version>` (matching the release's Git
tag), `sha-<short-commit>` (the first 7 characters of the release commit), and
`latest`. All three tags point at the same image content for that release; use
the version tag for a normal upgrade, the SHA tag to pin an exact commit
during a rollback rehearsal, and `latest` only for a first try or a
non-production demo - `docker-compose.yml` and `.env.example` default
`AGENT_STUDIO_VERSION` to `latest` so `docker compose up --wait` works before
any version pin is chosen, but a tracked deployment should set
`AGENT_STUDIO_VERSION` to an exact `v<version>` instead. Images build for
`linux/amd64`; `linux/arm64` is not yet published.

Every image carries standard OCI labels -
`org.opencontainers.image.version`, `.revision`, `.title`, and `.source` - set
from the same `VERSION` and `SHA` build inputs the
[release workflow](../../../.github/workflows/release.yml) uses for the
self-contained binaries. `agent-task-server` and `agent-host` additionally
answer their existing `--version` contract from inside the container, so the
value printed by `docker run ... task-server --version` /
`docker run ... agent-host --version` is the same
`<release>+sha.<commit>` identity `TaskServerBuildIdentity` and
`RunnerReleaseIdentity` report from a native install:

```bash
docker pull ghcr.io/agent-orc/agent-task-server:v1.4.0
docker inspect ghcr.io/agent-orc/agent-task-server:v1.4.0 \
  --format '{{index .Config.Labels "org.opencontainers.image.revision"}}'
docker run --rm ghcr.io/agent-orc/agent-task-server:v1.4.0 --version
```

The printed commit and the label's `.revision` value must match the release
commit and each other; a mismatch means the image was not built from the
tagged commit and should not be deployed.

Every image runs as a dedicated non-root user and declares a `HEALTHCHECK`
that exercises the same signal `docker compose ps` and an operator both read:
the open `/healthz` route for `agent-studio-api`, `agent-studio-web`,
`agent-task-server`, and `agent-studio-bff`; the existing `--health-check`
Task Server reachability probe for `agent-host` (it has no HTTP surface); and
a loopback TCP accept for `agent-orchestrator-engine`, which has neither an
HTTP surface nor filesystem access (it is a pure Task Server API client - see
`EngineContractTests.Engine_project_is_a_pure_contract_api_client`). Its
hosted `EngineHealthServer` listens on `HEALTH_PORT` and `--health-check`
treats a successful connect as live.

Each non-root user owns its data directories only inside the image; Docker
copies that ownership onto a named volume the first time the volume is empty.
A volume already populated by an older, root-owned image keeps root ownership
across the upgrade and needs one manual `chown` to the image's UID (`10001`
for every service except `agent-host`, whose `runner` user is also `10001`)
before the new container can write to it.

### Running one service from its image

Each image is runnable standalone with `docker run`, using the same
environment contract as its `deploy/release/agent-orchestrator/config/*.env.template`
or `deploy/release/agent-host/runner.env.template`:

```bash
docker run --rm -p 127.0.0.1:5071:5071 \
  -v agent-task-server-data:/var/lib/agent-orchestrator \
  -e AUTH=none \
  ghcr.io/agent-orc/agent-task-server:v1.4.0
```

### `docker-compose.yml` profiles

[`docker-compose.yml`](../../../docker-compose.yml) at the repository root
composes these images into four profiles, copy [`.env.example`](../../../.env.example)
to `.env` to override ports, the pinned `AGENT_STUDIO_VERSION`, and the
`distributed` profile's bearer credentials:

| Profile | Services | Purpose |
|---|---|---|
| (none) | `orchestrator-api`, `frontend` | The default install: a working Studio, pulling pinned images. See [Getting started](./getting-started.md). |
| `runner` | adds `agent-host-coding`, `agent-host-review` | Coding/review Agent Hosts against `orchestrator-api`. |
| `distributed` | `task-server`, `orchestrator-engine`, `studio-bff`, `agent-host-distributed`, plus the default two | The target architecture from [Distributed Agent Studio target architecture](../../concepts/distributed-agent-studio-target-architecture.md), previewed locally. |
| `dev` | a `-dev` sibling of every service above | Builds from this checkout's Dockerfiles instead of pulling. This is the only place `build:` is wired in the compose file; name the exact `-dev` services you want (e.g. `docker compose --profile dev up --build orchestrator-api-dev frontend-dev`) rather than a bare `--profile dev up`, which also starts every profile-less default service and collides on their ports. |

The disposable Compose topology explicitly sets
`ENGINE_ALLOW_INSECURE_HTTP=1` and `RUNNER_ALLOW_INSECURE_HTTP=1` only for
service-name traffic inside its private container network. Both opt-ins stay
disabled by default. A remote Task Server URL must use HTTPS.

`scripts/compose-smoke-test.sh` exercises all three non-dev topologies (default,
`distributed`, and a Task-Server-registered agent-host) by building through the
`dev` profile, so CI proves the Dockerfiles on every commit without needing
registry access.

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
| `STUDIO_AUTH_TOKEN_FILE` | One-time bootstrap input for the initial Studio principal | Generated and written by packaged setup |
| `ENGINE_AUTH_TOKEN_FILE` | One-time bootstrap input for the initial Engine principal | Generated and written by packaged setup |
| `STUDIO_AUTH_TOKEN`, `ENGINE_AUTH_TOKEN` | Direct bootstrap alternatives for ephemeral deployments | Unset |
| `BOOTSTRAP_RUNNER_ID` and `BOOTSTRAP_RUNNER_AUTH_TOKEN(_FILE)` | Optional bound Runner bootstrap for deterministic Compose or topology harnesses | Unset |
| `AUTH_TOKEN_FILE`, `AUTH_TOKEN` | Deprecated shared bearer input, mapped to the bootstrap Studio principal only | Unset |
| `TaskServer:MinimumLeaseSeconds` | Lower clamp for Runner leases | `30` |
| `TaskServer:MaximumLeaseSeconds` | Upper clamp for Runner leases | `600` |
| `TaskServer:ResultFinalizationMaxAttempts` | Bounded application-owned summary attempts after CORE completion | `3` |
| `TaskServer:InvariantReconciliationSeconds` | Interval for Tranche 0 invariant comparison | `30` |
| `TaskServer:InventoryGraceSeconds` | Minimum age before inventory mismatches are actionable | `120` |
| `TaskServer:MaximumEventPayloadBytes` | Hard UTF-8 size limit for one typed event payload | `262144` |
| `TaskServer:PrincipalRotationOverlapSeconds` | Default period during which the old credential remains valid after rotation | `300` |
| `TaskServer:MaximumPrincipalRotationOverlapSeconds` | Maximum accepted rotation overlap | `3600` |
| `TaskServer:RequireAuthentication`, `StudioBearerToken`, `RunnerBearerToken` | Deprecated compatibility profile mapped into persisted principals; removal follows Phase B migration | unset |
| `ARCHIVE_PATH` or `TaskServer:RetentionArchivePath` | Cold archive root for the SQLite retention adapter, outside `STORE_PATH`; `ARCHIVE_PATH` wins | `<STORE_PATH>/archive` |
| `ARCHIVE_S3_ENDPOINT` | Absolute `http(s)` endpoint for any S3-compatible service; enables the S3 target together with the three rows below | Unset |
| `ARCHIVE_S3_BUCKET` | Existing bucket for archive objects | Unset |
| `ARCHIVE_S3_PREFIX` | Optional object-key prefix | Empty |
| `ARCHIVE_S3_REGION` | SigV4 signing region | `us-east-1` |
| `ARCHIVE_S3_CREDENTIALS_FILE` | JSON file with `accessKey`/`secretKey`, mode 0640 or stricter; never stored in the policy or a manifest | Unset |
| `ARCHIVE_S3_PATH_STYLE` | Put the bucket in the URL path instead of the host; required for MinIO | `false` |
| `ARCHIVE_S3_SERVER_SIDE_CHECKSUM` | Also send the S3 SHA-256 checksum header, in addition to the end-to-end byte verification every target performs | `true` |
| `TaskServer:RetentionSchedulerEnabled` | Enables the daily archive sweep | `true` |
| `TaskServer:RetentionScheduleHour` | Server-local hour the scheduler checks once per day | `3` |
| `TaskServer:RetentionSchedulerIntervalMinutes` | Poll interval for the scheduler's daily-hour check | `60` |
| `TaskServer:RetentionMaximumLoadPerCore` | Maximum one-minute Linux load average per logical core for a scheduled run; unavailable load telemetry is admitted and logged | `1.5` |
| `TaskServer:RetentionIntegritySampleCount` | Maximum manifests in the weekly integrity sample | `25` |
| `TaskServer:RetentionIntegrityDayOfWeek` | Server-local weekday for the integrity sample | `Sunday` |
| `TaskServer:BackupPathFull` | Full backup set root | `<BACKUP_PATH>/full` |

Every `ARCHIVE_S3_*` value stays unset by default, which keeps
`archiveTarget: local` behavior unchanged; local-only archiving needs no S3
configuration. See
[control-plane-docker.md, "Cold archive target"](./control-plane-docker.md#cold-archive-target)
for the Docker control-plane variables and the packaged installer prompts,
and the
[retention and archive dossier, §5](../retention-und-archiv/index.html#archiv-s3)
for the full design rationale.

- Configure at most one direct value or file for each bootstrap principal.
- `GET /api/v1/protocol` and `POST /api/v1/protocol/compatibility` remain open
  so a client can negotiate before registration. All other v1 requests require
  a valid persisted principal when `AUTH=bearer`. Missing, malformed, unknown,
  expired, or revoked credentials return 401. Authenticated principals without
  the route scope return 403.
- `X-Client-Id` and `X-Actor-Id` remain attribution hints. Neither participates
  in authentication or scope decisions.
- Studio defaults to `tasks:read`, `tasks:write`, `management`, and
  `events:subscribe`. Engine defaults to orchestration claim and settlement plus
  required reads. Each Runner receives a bound principal for Runner claim,
  review claim, lease, event, artifact, and completion operations. Studio has
  no fence-minting claim scope; Engine cannot claim Runner work; Runner has no
  Studio mutation, management, or hub subscription scope.
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

The canonical production bootstrap uses distinct Studio and Engine credentials
with `AUTH=bearer`. Setup exchanges its protected join credential for one bound
Runner credential and writes that result to `RUNNER_AUTH_TOKEN_FILE`. The
interim `TaskServer:RequireAuthentication`, `StudioBearerToken`, and
`RunnerBearerToken` profile is deprecated and scheduled for removal after
existing Phase B installations migrate. Do not configure both modes. Studio
BFF reads
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

## Rotate and revoke principals

Use a current `management` credential and protocol header. Creation and
rotation reveal a new secret exactly once, so redirect the response to a
protected file and install the credential before the overlap ends.

```bash
curl --fail --silent --show-error \
  -H "Authorization: Bearer $MANAGEMENT_CREDENTIAL" \
  -H "X-Task-Protocol-Version: 2" \
  -H "Content-Type: application/json" \
  -d '{"overlapSeconds":300}' \
  https://task-server.example/api/v1/management/principals/runner:agent-runner-01/rotate \
  > /root/runner-rotation.json
```

Replace the Runner token file atomically, restart the Runner, and prove it has
registered before the overlap expires. A zero-second overlap invalidates all
older credential versions immediately. To contain a compromise, revoke first;
revocation is read from the store on the next request and needs no Task Server
restart:

```bash
curl --fail --silent --show-error -X POST \
  -H "Authorization: Bearer $MANAGEMENT_CREDENTIAL" \
  -H "X-Task-Protocol-Version: 2" \
  https://task-server.example/api/v1/management/principals/runner:agent-runner-01/revoke
```

After revocation, reconcile or fence any active attempt separately. Revoking a
credential prevents new authenticated requests; it does not by itself prove
that a process stopped or that an existing Git credential was contained.

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

## Studio core-attach bundle (P0)

AGT-2755 implements the 27-route P0 "core-attach" bundle from the
[Studio route ownership](../../studio-route-ownership/index.html) dossier:
human login/session bootstrap, the lane-grouped board projection, task
lifecycle mutation, orchestrator chat and context digests, runner status, and
the replayable event stream behind `/hubs/jobs`. The remaining four routes in
that bundle (`GET`/`POST /api/v1/workspaces`, `GET`/`POST /api/v1/projects`)
already existed in standalone v1 and are not duplicated here. Handlers live in
`StudioEndpoints.cs`, `StudioLifecycleCoordinator.cs`,
`TaskServerStudioAuthStore.cs`, `TaskServerStudioTaskLifecycleStore.cs`,
`TaskServerStudioOrchestratorStore.cs`, `TaskServerStudioProjectionsStore.cs`,
and `TaskServerStudioEventStreamStore.cs`; wire contracts live in
`StudioContracts.cs`.

| Route | Purpose | Scope |
|---|---|---|
| `GET`/`POST /api/v1/studio/auth/{status,bootstrap,login,logout,change-password}` | Human Studio session lifecycle | `tasks:read` / `tasks:write` |
| `GET /api/v1/studio/board` | Lane-grouped board projection across every project | `tasks:read` |
| `GET/POST /api/v1/studio/orchestrator/context/{global,project:{id},task:{id}/{id}}[/refresh]` | Orchestrator context digest, matching the legacy `OrchestratorApi` context-key shape | `tasks:read` / `tasks:write` |
| `GET /api/v1/studio/orchestrator/sessions` | Orchestrator session listing, derived from durable orchestrator contexts | `tasks:read` |
| `GET /api/v1/studio/runner/status` | Active runs grouped by project | `tasks:read` |
| `GET`/`POST /api/v1/studio/runner/{project}/orchestrator-chat[/attachments[/{fileName}]]` | Orchestrator chat send/read and image attachment upload/download | `tasks:read` / `tasks:write` |
| `DELETE`/`POST`/`PUT /api/v1/projects/{projectId}/tasks/{taskId}/{-,move,move-to-top,start,state,stop,continue}` | Task lifecycle mutation | `tasks:write` |

A human Studio session (`ts-studio-session` / `ts-studio-csrf` cookies, or the
`X-Studio-Session-Token` header) is a second, nested identity layer above the
existing machine-principal bearer: the connector still authenticates every
call with its own scoped bearer, and a signed-in Studio user lives underneath
that call. This mirrors the nested-identity note in
[remote Task Server, local Studio](../remote-task-server-local-studio.md).

Every task lifecycle mutation appends one durable, cursor-ordered
`studio_stream_events` row after its owning mutation commits, then publishes
it live over `/hubs/v1/studio` (`TaskServerStudioHub`, mapped beside the
existing `/hubs/events`). This is the standalone Task Server's compatibility
path for the legacy `/hubs/jobs` SignalR feed named in the connector dossier:
a client that supplies `?cursor=<last-seen-cursor>` on connect receives every
missed event, in cursor order, before any live event, so a Studio-detached
period (backend restart, Windows sleep, lost network) never loses a
mutation and never needs a full board re-pull to catch up. The hub is a
notification feed only; it is written after the owning domain mutation has
already committed and is never a second source of truth.

### Unscoped-project compatibility token

The task lifecycle routes above are project-scoped
(`/api/v1/projects/{projectId}/tasks/{taskId}/...`), matching every other
task-owned v1 route. The legacy Angular frontend calls their pre-cutover
equivalents (for example `POST /api/tasks/{taskId}/move`) with only a task id;
the OrchestratorApi connector profile (AGT-2754) is a mechanical path
translator with no task-to-project lookup of its own, so it cannot fabricate
a real project id for these calls. The connector substitutes the reserved
literal `-` for `{projectId}` in that case
(`ConnectorProxy.UnscopedProjectToken`), and the Task Server resolves the task
by id alone when it sees that literal (`TaskServerStore.UnscopedProjectToken`)
rather than treating `-` as an unknown project. A real project id is never
this literal, so the substitution cannot collide with an actual project. Every
other v1 route, and every call that already supplies a real project id or the
`?project=` query compatibility fallback, is unaffected.

## Studio operations-and-insight bundle (P2)

AGT-2757 implements the 102-route P2 "operations and insight" bundle from the
[Studio route ownership](../../studio-route-ownership/index.html) dossier:
bus, runtime, cycle time, token, deployment, security review, analysis,
drift, supervisor, and recovery projections, plus the project-settings,
admin, and CLI mutations D4b groups into the same bundle. Handlers live in
`StudioOperationsAndInsightEndpoints.cs` (route mapping only) and a set of
domain-named `TaskServerStudio*Store.cs` partial-class files
(`TaskServerStudioOperationsStore.cs`, `TaskServerStudioSettingsStore.cs`,
`TaskServerStudioAdminStore.cs`, `TaskServerStudioInsightProjectionsStore.cs`,
`TaskServerStudioSupervisorStore.cs`,
`TaskServerStudioDriftAnalysisSecurityStore.cs`,
`TaskServerStudioDesignProposalsPublishStore.cs`,
`TaskServerStudioMiscStore.cs`); wire contracts live in
`StudioOperationsContracts.cs`. All routes are mounted under
`/api/v1/studio/**`, matching every route's `targetRoute` in
[`routes.json`](../../studio-route-ownership/routes.json).

### Durable projections, not a second disk-backed store

Every P2 legacy handler in `OrchestratorApi` reads or writes a disk-backed
tree under `IConfiguration["TaskRepository"]` (Markdown reports, JSONL bus
logs). The standalone Task Server has no such tree, so this bundle does not
port that storage model; it represents the same information as SQLite state,
following three patterns depending on what the route actually needs:

1. **Generated or reviewed items** (analysis reports, drift reports and
   architecture-drift actions, security reviews, design council items and
   actions, proposals, publish/deployment runs, skill-readiness fixes, wiki
   grading runs, admin prompt review/rebaseline) are rows in one shared
   ledger table, `studio_operations` (id, project id, domain, kind, title,
   dispatched task id, status, request/result JSON, timestamps). List and
   detail routes read this table directly; accept/decision routes are a
   plain status update on the same row.
2. **Live feeds** (bus messages, runtime events, token usage, test runs,
   regression-radar findings, visual evidence, the global pipeline alert)
   read the existing, already-fenced `events` table, filtered by a
   `studio.*` event-kind convention (`StudioInsightEventKinds`). A Runner
   reports these the same way it reports any other typed event, over the
   existing `POST /api/v1/runs/{runId}/events` contract; there is no second
   ingestion path.
3. **Computed projections** (cycle time, throughput, supervisor observation
   and meta-cycle) are plain queries over the existing `tasks`, `runs`,
   `leases`, and `audit` tables - the same "fold what already exists rather
   than track a second copy" rule the P0 board and runner-status projections
   follow.

A handful of routes own small, purpose-built tables because they are
genuinely structured state with no natural home in the patterns above:
`studio_project_settings` (one row per project: auto-commit, auto-push
strategy, CLI context/mode, crash-recovery flag, lane-sort strategy, max
parallelism, orchestrator model, quota-wait policy, publish automation,
supervisor pickup-paused), `studio_project_urls`,
`studio_ownership_mappings` (also the source `component-routing/resolve`
matches against), `studio_prompts`, `studio_architecture_elements`,
`studio_crash_recovery_decisions`, `studio_visual_evidence_acks`,
`studio_watch_paths`, `studio_cli_settings`, `studio_admin_settings`, and
`studio_schedules`.

### Dispatch through the existing fenced Runner contracts

Every route whose computation needs a checkout or a CLI call (an analysis or
drift report, a security review, a design or proposals action, a publish or
deployment run, a skill-readiness fix, a wiki grading run, an admin prompt
review) dispatches by creating a normal task in the `2-ready` lane through
the existing `TaskServerStore.CreateTaskAsync`, exactly as a human-started
task would. A Runner claims it through the existing
`/runners/{runnerId}/claims`, lease, event, and artifact contracts - the same
fenced authority every other run already goes through. This bundle adds no
new dispatch mechanism and the connector never spawns or supervises this
work itself; the connector remains a credential and transport boundary, and
the workflow (ready lane to claim to lease to completion) lives entirely in
the Task Server's pre-existing run lifecycle.

A small coordinator hook closes the loop without touching the shared
completion method's internals: `TaskServerEndpoints`'s
`POST /api/v1/runs/{runId}/completion` handler calls the existing
`TaskServerStore.CompleteRunAsync` first, and only once that commit succeeds
does it call `TaskServerStore.TryMaterializeStudioOperationForRunAsync` to
fold the run's terminal status and artifact list into the owning
`studio_operations` row - the same "commit the domain mutation first, then
append a bounded side effect" ordering `StudioLifecycleCoordinator` uses for
P0. Generated content itself is never duplicated into `studio_operations`;
detail routes reference the run's artifacts through the existing artifact
content route.

One route, `POST /api/v1/studio/drift/actions/code-pattern-drift`, evaluates
a small built-in rule set in process and records a `studio_operations` row
with `status: completed` immediately, mirroring the legacy handler's
deterministic, no-LLM behavior; it is the one generated-item route that never
dispatches.

### Split project snapshot

The concept dossier calls out the legacy project snapshot as a mixed
contract that must split before cutover: task authority facts belong to the
Task Server, but working-tree/checkout facts stay with the dev-seat
connector. `GET /api/v1/studio/projects/{project}/snapshot` returns only the
authority half (project identity, lane-grouped task counts, durable
settings); it does not attempt to represent checkout state that the Task
Server has no way to observe.

| Route | Purpose | Scope |
|---|---|---|
| `PUT /api/v1/studio/admin/config/orchestrator` | Default orchestrator model/thinking-level config | `management` |
| `DELETE`/`PUT /api/v1/studio/admin/prompts/{name}` | Named prompt document CRUD | `management` |
| `POST /api/v1/studio/admin/prompts/{name}/{preview,rebaseline,review}`, `POST .../prompts/review-all` | Prompt preview render and review dispatch | `management` |
| `GET`/`POST /api/v1/studio/analysis/{project}/reports[/{reportId}]`, `GET`/`PUT .../schedule` | Analysis report dispatch/list/detail and schedule | `tasks:read` / `tasks:write` |
| `GET /api/v1/studio/bus/{project}/{messages[/{id}],recent,summary,token-aggregate}` | Agent message bus projections | `tasks:read` |
| `PUT /api/v1/studio/cli/{model-routing/economy-mode,quota/caps,quota/model-routes,quota/wait-policy}` | Instance-wide CLI quota and routing settings | `management` |
| `POST /api/v1/studio/component-routing/resolve` | Deterministic path-to-owner match against ownership mappings | `tasks:write` |
| `GET /api/v1/studio/crash-recovery/pending`, `POST .../pending/{id}/{commit,dismiss}` | Expired-lease recovery queue, computed from `leases` | `tasks:read` / `tasks:write` |
| `POST /api/v1/studio/drift/actions/code-pattern-drift[/rules]`, `POST /api/v1/studio/drift/{project}/actions/{action}[/prompt]`, `GET/POST .../architecture[/{modelId}/elements/{elementId}/status]`, `GET .../reports[/{reportId}]` | Drift actions, architecture element status, and reports | `tasks:read` / `tasks:write` |
| `GET /api/v1/studio/pipeline/accepted-integration-alert` | Instance-wide latest accepted-integration alert | `tasks:read` |
| `GET /api/v1/studio/runtime/{project}/events` | Runtime event feed | `tasks:read` |
| `POST /api/v1/studio/supervisor/{project}/intervene/{cancel-run,force-fail,pause-pickup,resume}`, `GET .../{meta-cycle,observation,recent-events}` | Supervisor interventions and observation | `tasks:write` / `tasks:read` |
| `POST /api/v1/studio/prompt/enhance`, `POST /api/v1/studio/title/generate` | Instance-wide text helpers | `tasks:write` |
| `POST /api/v1/studio/token-pricing/calculate` | Pure token-cost calculator | `tasks:write` |
| `POST/DELETE /api/v1/studio/watch-paths[/{name}]` | Watch path registration | `tasks:write` |
| `PUT`/`DELETE /api/v1/studio/projects/{projectId}`, `PUT .../ownership-mappings/{mappingId}`, `POST/PUT/DELETE .../urls[/{urlId}]` | Project identity, ownership mapping, and URL mutation | `tasks:write` |
| `PUT /api/v1/studio/projects/{project}/{auto-commit,auto-push-strategy,cli-context-mode,cli-mode,crash-recovery,lane-sort-strategy,max-parallelism,orchestrator-model,quota-wait-policy}` | Per-project Studio settings | `tasks:write` |
| `GET /api/v1/studio/projects/{project}/{cycle-time[/tasks/{taskKey}],throughput,snapshot,regression-radar,test-runs}` | Computed and event-backed project projections | `tasks:read` |
| `GET/POST .../visual-evidence[/{itemId}/acknowledge]`, `GET .../token-usage/{expensive,heatmap,job/{taskId},pipeline-cost,summary}` | Visual evidence and token-usage projections | `tasks:read` / `tasks:write` |
| `POST .../deployment/compile`, `GET .../deployment/summary` | Deployment dispatch and summary | `tasks:write` / `tasks:read` |
| `POST .../design/actions/{action}`, `GET .../design/{council[/{fileName}],overview,references}`, `POST .../design/council/{fileName}/accept` | Design council and actions | `tasks:write` / `tasks:read` |
| `DELETE .../proposals[/{proposalId}]`, `POST .../proposals/{generate,refine-feedback}`, `POST .../proposals/{proposalId}/decision` | Proposal generation, decision, and deletion | `tasks:write` |
| `PUT .../publish/automation`, `POST .../publish/{package,website}` | Publish settings and dispatch | `tasks:write` |
| `POST .../queue-health/repair` | Releases expired leases for the project | `tasks:write` |
| `POST .../security/audit`, `GET .../security/{baseline,reviews[/{fileName}]}` | Security review dispatch, baseline, and list | `tasks:write` / `tasks:read` |
| `POST .../skill-readiness/fix-task` | Skill-readiness fix dispatch | `tasks:write` |
| `POST .../wiki/grading/{abort,run}` | Wiki grading dispatch and abort | `tasks:write` |

No new `TaskServerScopes` member was added; every route reuses `tasks:read`,
`tasks:write`, or `management`, matching P0's precedent that this bundle
needs no new scope taxonomy.

## Backup and restore rehearsal

`POST /api/v1/management/backups` creates a consistent SQLite backup, runs an
integrity check, and returns its SHA-256. Backups contain server/workspace/
project/task/run identities, task state, events, artifact content, audit,
principal and credential hashes, Runner records, coding and review leases,
immutable review subjects, fenced reports, and fence counters.

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

## Deployment regression scenario rehearsal

The topology rehearsal above proves individual failure modes (outage,
transport interruption, HTTPS auth). The **deployment regression scenario**
(AGT-2739) is the complementary end-to-end proof: one seeded fixture driven
through bootstrap, claim, run, auto-review, backup, and restore in one
ordered pass, in three targets (`inproc`, `compose`, `remote`) from one
definition. It is the gate every deployment card and every release proves
itself against. See
[docs/operations/testing/deployment-scenario.md](../testing/deployment-scenario.md)
for what it proves, how to run each target, and how to add a step.

```bash
scripts/scenario.sh --target inproc --level smoke
```
