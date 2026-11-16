# Managed databases for hosted apps (IP-D5 option B)

The owner chose option B for IP-D5 on 16 November 2026: the run zone
provisions a managed database for a hosted app. AGT-3877 (MD-1) delivers
provisioning, credentials, the Bereitstellung view and deletion protection.
AGT-3878 (MD-2) adds [backups](#backups), the [restore runbook](#restore-runbook)
and [health and size](#health-and-size). The operator-attached database of
option A ([hosted data](../hosted-data/README.md)) stays available.

## Declaring a database

A hosted app declares its databases in `.agent-studio/project.yml` schema 2:

```yaml
databases:
  - name: orders
    engine: postgres
    version: "16.4"
    size: small
    variable: DATABASE_URL
    environments: [staging, production]
```

| Key | Meaning |
|---|---|
| `name` | Name of the database, its volume and its connection file: a lower-case letter, then lower-case letters, digits or underscores (up to 31). Unique per environment. |
| `engine` | `postgres`. |
| `version` | PostgreSQL major version or major.minor, 14 to 17, such as `16` or `16.4`. |
| `size` | `small` (0.5 CPU, 512 MB, 256 processes), `medium` (1 CPU, 1 GB, 512) or `large` (2 CPUs, 4 GB, 1024). Default `small`. |
| `variable` | Name of the connection string. Default `DATABASE_URL`. Two databases of one environment need different names. |
| `environments` | `staging`, `production`, or both; omitted means every environment. |

Validation lives in `contracts/TaskServer.Contracts/ProjectDatabaseDefinition.cs`
and the JSON schema is
[project-execution.schema.json](../../app/schemas/project-execution.schema.json).
A database may apply only to environments hosted in the run zone, that is
container environments declared with `"destination": "edge"`
([run-target host](../../system/domains/runner.md#run-target-host-app-hosting-ip-1)).
An environment outside the run zone that a database applies to is reported as
unusable; scope the entry with `environments`. When schema 2 project.yml overlays an
edge-hosted declaration, the declaration keeps its own template and gets no
SSH steps, because the run-target host executes and reports its deploy jobs. Secret names starting with
`MANAGED_DB_` are reserved for Studio, and `variables` may not declare the
connection string's name or its `_FILE` name.

## Provisioning on deploy

A deploy job stores the databases with the rest of its approved snapshot, so a
change to project.yml reaches the run host only through a deploy. On the job's
first poll the host provisions each database before it starts the app
revision that uses it:

- One `postgres:<version>-alpine` container per database, named
  `<app key>-db-<hash>`, with the declared CPU, memory and process limits,
  `no-new-privileges`, and `NET_RAW` and `MKNOD` dropped.
- One named Docker volume per database (`<container>-data`). The compose file
  declares it external, so no compose command removes it.
- One internal Docker network per environment (`<app key>-data`). The
  database container joins only this network and publishes no port. The
  environment's app containers join it next to their own network. The edge
  never joins it, and no other environment's app does, so neither public
  traffic nor another environment can reach the database (the zone rules of
  AGT-3231 and AGT-3241).
- Readiness is `pg_isready` over TCP inside the container, so the image's
  initialisation server does not count as ready.

The host reports each database on the observed-state route as `provisioning`,
`running`, `unhealthy`, `failed`, `retained`, `upgrade-refused` or `deleted`,
with its version and the size of its data directory, measured at most every
five minutes.

## Credentials

Studio generates a 32-byte password for each database before it answers the
host's poll. It stores the value through the secret provider (AGT-3222) as
`secret://<project>/<environment>/MANAGED_DB_<NAME>_PASSWORD` and binds that
reference to the environment (AGT-3246), recorded as
`studio:managed-database`. No person chose or saw the value, so a production
binding needs no separate approver.

The host resolves the reference with
`POST /api/runner/secrets/resolve` and the workload
`{ "kind": "run-target", "project", "environmentId" }`. Studio issues it only
to a credential with `runner.deploy` whose Runner ID is the environment's
`targetHostId`, and only for the databases of the open or running deploy of
that environment. The value exists on the host in the agent's memory and in
tmpfs files under `RUN_TARGET_SECRET_RUNTIME_DIR`:

- the database container reads `/run/secrets/password` through
  `POSTGRES_PASSWORD_FILE`;
- the app container gets `<variable>_FILE`, for example `DATABASE_URL_FILE`,
  the path of a file that holds
  `postgresql://app:<password>@<container>:5432/<name>`.

Like every run-zone secret, the connection string never enters a compose
file, Docker's container configuration, host state, Studio's state, logs or
the UI; host diagnostics are masked. project.yml and the Bereitstellung view
show only the reference. The agent keeps an issued value in memory, so a
crashed database or app restarts while Studio is unreachable. After an agent
restart, a restart that needs the value waits until Studio answers again.

## Upgrades

A changed minor version or size is a deploy step: the host recreates the
container on the same volume and checks readiness again. A changed major
version needs a dump and restore, which a later card adds. Studio refuses
such a deploy before the host sees it, using the version the host reports;
the job fails with `deploy-step-failed` and the database shows the message.
The host refuses it as well and leaves the data untouched.

## Removal and deletion

Removing a database from project.yml deletes nothing. After the next deploy
is live without it, the host stops the database container and keeps the
volume; Studio shows the database as retained. When an environment leaves
the host, its database containers and data network go and the volumes stay.

Deleting the data is a separate owner action in the Bereitstellung area
(Deploy, Operations). Only a retained database offers it, and the owner types
the database name to confirm. Studio accepts the request only when neither
the running deploy nor an open one declares the database. The host then
removes the container, every backup of the database and the volume, and
confirms with `deleted`; Studio removes the generated credential and its
binding afterwards. A host never deletes a database that its desired state
still declares.

## Backups

Implemented choice: logical dumps, no WAL archiving yet. Each dump is a
PostgreSQL custom-format archive (`pg_dump --format=custom`), at least daily
by default. A dump restores into any server of the same or a later major
version, so it also serves the dump-and-restore path of a later major
upgrade, and one restore procedure covers every case. WAL archiving with
point-in-time recovery was not built in this card. It needs physical base
backups and a WAL archive next to the dumps, and a restore that replaces the
data directory of a stopped server. A failing `archive_command` also fills
the volume until the server stops. None of that can be exercised with the
fake run host, and getting it wrong would endanger the data it is meant to
protect, so it is left for a follow-up. Until then the recovery point is the
backup interval: an environment that cannot lose a day sets a shorter
`backupIntervalHours`.

The host runs the server's own tools inside the database container through
`docker exec`. The dump is written as `<id>.dump.partial`, its table of
contents is read back with `pg_restore --list`, and only then is it renamed
to `<id>.dump` and listed. An id is the UTC time and the kind, for example
`20261116T020000Z-scheduled`; kinds are `scheduled`, `manual` (Back up now)
and `pre-restore`.

Dumps are stored outside the database volume, in a host directory that each
database container of the environment mounts at `/backups`:
`RUN_TARGET_BACKUP_DIR/<app key>/<database>/<id>.dump`. The default
`RUN_TARGET_BACKUP_DIR` is `$RUNNER_STATE_DIR/run-target-backups`; an
operator should point it at another disk or a mounted backup target, since a
dump on the volume's disk does not survive the loss of that disk. It must be
an absolute path outside `RUN_TARGET_STATE_DIR`. The first host version with
backups writes a new compose file for every database, so each database
container is recreated once on the same volume.

The host dumps a database as soon as it runs, then whenever its newest dump
is `backupIntervalHours` old. A failed attempt is retried after 30 minutes.
A cycle takes at most one scheduled dump, so a large database does not hold
up deploys and crash supervision for longer than one dump. Dumps and
retention also run while Studio is unreachable; the host keeps the last
policy Studio sent. A dump is kept while it is younger than
`backupRetentionDays` or one of the `backupsKept` newest, so a host that
cannot dump for weeks still keeps its last good dumps. Each `docker exec`
has a 10-minute limit; a dump that takes longer fails and raises an alert.

The retention policy belongs to the environment. The optional
`managedDatabases` object of an edge-hosted environment declaration
overrides the defaults of its kind:

```json
{ "managedDatabases": { "backupIntervalHours": 6, "backupRetentionDays": 30, "backupsKept": 7, "volumeAlertMb": 8192 } }
```

| Key | Default | Range |
|---|---|---|
| `backupIntervalHours` | 24 | 1 to 168 |
| `backupRetentionDays` | 30 in production, 7 elsewhere | 1 to 3650 |
| `backupsKept` | 7 in production, 3 elsewhere | 1 to 100 |
| `volumeAlertMb` | by size: small 5 GB, medium 20 GB, large 80 GB | 64 and more |

Studio reads the policy from the current declaration on every poll; unlike
the databases themselves it does not wait for a deploy. An environment
outside the run zone may not declare it.

The Bereitstellung view shows for each database the last backup time, the
backup status (`ok`, `pending` before the first dump, `overdue`, `failed`,
`none` when no dump is expected), the last error, the policy and the dumps
with their size and verification.

### Backup alerts

Studio checks every declared, running database on each health tick. A
backup is overdue when the newest dump, or the first time the database ran,
is older than the interval plus one hour. A failed attempt after the newest
dump is its own alert and stands in for overdue while it lasts. An alert
creates one Backlog card tagged `environment-health` and an Activity alert,
the path of the AGT-3223 health incidents; when the condition ends, the card
gets a comment and Activity a recovery entry. A condition that returns while
its earlier card is still open comments on that card. Studio never moves
the cards.

## Restore runbook

This is the AGT-3247 runbook for a managed database. Restores run on the run
host through Studio, because the database publishes no port and the host has
no SSH runbook step.

1. Open Deploy, Operations, and find the database. Pick a backup.
2. Run **Test restore** first. The host starts a throwaway
   `postgres:<version>-alpine` container without a network that mounts only
   this database's backup directory read-only, restores the dump with
   `pg_restore --exit-on-error`, counts the restored tables, and removes the
   container. The backup row shows "Verified" with the table count; a failure
   names the `pg_restore` error. The live database is not touched.
3. Choose **Restore** and the target:
   - *This database* replaces the live data with the backup.
   - *Another database* of the same environment restores into it instead,
     for example to inspect old data next to the live one. Declare it in
     project.yml (`databases`) and deploy first; the app reaches it through
     its own variable. Its current data is replaced.
   Type the target's name to confirm.
4. In production the restore waits for an Owner's approval in the same view
   (Approve or Reject); a request that is not approved within 7 days is
   rejected. Elsewhere it goes to the host on its next poll. No restore is
   accepted while a deploy of the environment is open.
5. On the host the restore
   1. dumps the target's current data (`pre-restore`); if that fails, nothing
      is restored;
   2. creates a new database next to the target and loads the backup into it;
      if the load fails, it is dropped and the live data is unchanged;
   3. stops new sessions on the target (`ALLOW_CONNECTIONS false`), ends the
      open ones, and swaps the names, so the app's connection string reaches
      the restored data when it reconnects;
   4. drops the replaced database. Its data is in the `pre-restore` dump.
6. The operation shows `succeeded` with the dump that holds the replaced data,
   or `failed` with the reason. To undo a restore, restore that
   `pre-restore` dump the same way. An operation without a host answer
   fails after 6 hours.

The app sees its connections end during the swap. Expect a few seconds of
failed queries, or stop traffic first in a maintenance window.

If Studio itself is unavailable, an operator on the run host can restore a
dump by hand into a new database of the same container, for example
`docker exec <container> createdb -U app orders_copy` and
`docker exec <container> pg_restore -U app -d orders_copy --no-owner /backups/orders/<id>.dump`.

## Health and size

Every cycle, also while Studio is unreachable, the host checks each running
database with `pg_isready` over TCP and reports `ready`. A database that
stops answering is `unhealthy` until it answers again; the next deploy
convergence also restarts its container. Once a minute it counts client
connections and reads `max_connections`; every five minutes it measures the
data directory.

Liveness from outside the run host: the AGT-3223 control-plane checks get one
derived `database-<name>` check per managed database (kind `database`),
added even when `healthChecks` turns the network checks off. It opens no
connection, since the database publishes no port. It fails when the host
reports the database not ready (`database-not-ready`) or failed
(`database-failed`), or when no report arrived for three minutes
(`database-report-stale`), which is what a stopped host or a lost route to it
looks like from the control plane. Its incidents, uptime and cards are those
of every other check. Before the first deploy it reports `not-applicable`,
which counts toward nothing.

Each report that changes a database writes gauges into the environment's
operations intake (AGT-3227, [app telemetry intake](../setup/app-telemetry-intake.md)), at
most once a minute when nothing changed, with the attributes
`db.system.name=postgresql`, `db.namespace=<name>` and
`agent_studio.run_host`:

| Metric | Unit | Meaning |
|---|---|---|
| `agent_studio.managed_database.volume.size` | By | Size of the data directory |
| `postgresql.backends` | {connection} | Client connections |
| `postgresql.connection.max` | {connection} | `max_connections` |
| `agent_studio.managed_database.ready` | 1 | 1 when the last readiness check passed |
| `agent_studio.managed_database.backup.age` | s | Age of the newest dump |
| `agent_studio.managed_database.backup.size` | By | Size of the newest dump |

When the data directory reaches the environment's volume alert threshold,
Studio raises a `volume-threshold` alert with its own card, as for backups.

## Studio HTTP contract

| Method and path | Request | Response |
|---|---|---|
| `GET /api/projects/{project}/environments/{environmentId}/databases` | None | `200` with `databases`: `name`, `engine`, `version` (approved), `runningVersion`, `size`, `resources`, `variable`, `credentialReference`, `state`, `declared`, `volumeBytes`, `observedAt`, `message`, `deletionRequestedBy`, `deletionRequestedAt`, `deletedAt`. `404` for an undeclared environment. |
| `POST /api/projects/{project}/environments/{environmentId}/databases/{name}/deletion` | `{ "confirm": "<name>" }` | `202` when requested. `403` without a signed-in Owner, `400` when `confirm` differs from the name, `404` for an unknown database, `409` while a deploy declares it or when the deletion is already requested or done. |
| `POST /api/projects/{project}/environments/{environmentId}/databases/{name}/operations` | `{ "kind": "backup" }`, `{ "kind": "verify", "backupId" }` or `{ "kind": "restore", "backupId", "targetDatabase", "confirm" }` (`targetDatabase` defaults to `name`, `confirm` repeats it) | `202` with `operation`. `403` without a signed-in user, `400` for an unknown kind or a `confirm` that differs, `404` for an unknown environment or database, `409` while another operation of the database is open, for an unknown backup, a target that is not declared and running, or an open deploy. |
| `POST .../databases/{name}/operations/{operationId}/approve` and `.../reject` | None | `200` with `operation`. `403` without a signed-in Owner, `404` for an unknown operation, `409` when it is not awaiting approval or the restore is no longer possible. |

The view's `state` adds `pending-deploy` (declared, no deploy yet),
`retained` and `deleting` to the host states. Since AGT-3878 each database
also has `environmentKind`, `ready`, `connections`, `maxConnections`,
`backupPolicy` (`intervalHours`, `retentionDays`, `minimumKept`),
`volumeAlertBytes`, `lastBackupAt`, `backupStatus`, `lastBackupError`,
`lastBackupAttemptAt`, `backups` (newest first: `id`, `takenAt`, `bytes`,
`kind`, `verifiedAt`, `verifiedTables`) and `operations` (the newest ten that
read or replace it: `id`, `kind`, `backupId`, `sourceDatabase`,
`targetDatabase`, `state`, `requestedBy`, `requestedAt`, `approvedBy`,
`approvedAt`, `finishedAt`, `message`). An operation `state` is
`awaiting-approval`, `queued`, `succeeded`, `failed` or `rejected`; Studio
keeps the newest 200 finished operations per project as their audit trail.
The public demo execution profile denies the deletion and the three
operation routes.

## Run-target protocol

The desired state carries `databaseOperations` (`id`, `project`,
`environmentId`, `name`, `kind`, `backupId`, `targetName`): queued operations
of this host's databases, a restore only once approved. Each database spec
carries `backup` (`intervalHours`, `retentionDays`, `minimumKept`). The
observed state adds per database `ready`, `connections`, `maxConnections`,
`backups`, `lastBackupAttemptAt` and `lastBackupError`, and a list
`databaseOperations` of results (`id`, `state` `succeeded` or `failed`,
`message`, `finishedAt`, `backupId`). The host runs each operation id once and
repeats its result while Studio still sends the operation. Studio sanitizes
the report: backup ids of the host's own shape, closed kind and state
vocabularies, no time in the future, bounded counts and a masked one-line
message of at most 300 characters.
