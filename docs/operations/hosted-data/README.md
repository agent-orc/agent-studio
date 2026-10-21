# Hosted application data (IP-D5 option A)

An app declares persistent data in its repository under
`docs/deployments/environments/<id>.json`. Only container environments accept
`data`. For example:

```json
{
  "data": {
    "database": {
      "secretRef": "secret://demo/stage/DATABASE_URL",
      "environmentVariable": "DATABASE_URL"
    },
    "volumes": [
      { "name": "uploads", "mountPath": "/app/uploads" }
    ]
  }
}
```

The JSON above is the `data` portion of a complete environment declaration.
The database is supplied by the operator. Studio stores and displays only the
AGT-3222 secret reference, never the connection value. The reference must name
the same project and environment as the deploy job. Its secret name joins the
job's allowlist for workload resolution. This option does not provision or back
up the external database.

Volume names are stable identities within a project and environment. A release
snapshot and its deploy job carry the declarations, including on rollback.
An executor maps a declared volume to the same host directory on every release;
it must not put that directory inside a release image or remove it on a deploy.
The filesystem adapter uses `<volume-root>/<project>/<environment>/<volume>`.

Studio's Deploy > Operations view shows the database binding, declared volumes,
daily UTC backup schedule, backups, restore action and an audit trail. Schedule
state is stored beneath the configured `TaskRepository/hosted-volume-backups`.
The scheduler tries each enabled volume once per UTC day at or after its selected
hour and records failures as attempts. A co-located installation can set
`HostedData:LocalVolumeRoot` to enable the filesystem adapter; archives are
stored beneath `TaskRepository/hosted-volume-archives`. Restore verifies the
archive SHA-256 and extracts to a staging directory before replacing the live
directory. Each volume retains its 14 newest backup records and archives.
Archive pruning is retried on the next backup if storage cleanup fails. A
failed prune is recorded in durable schedule state and blocks new archives for
that volume until the retry succeeds. The schedule exposes `pendingPrune` and
`lastError` while cleanup is outstanding. A
backup has a 10-minute limit; scheduled backups use at most four concurrent
operations so a stalled volume does not hold up another project's backup.
Backup and restore operations serialize per volume. A restore in progress does
not hold the Studio state lock, so schedules, audit reads, and operations on
other volumes can continue while the host restores data.
The app must be quiescent for a consistent backup and restore.

## Studio HTTP contract

All routes are under
`/api/projects/{projectName}/environments/{environmentId}/volumes`.
`projectName`, `environmentId`, and `volume` identify repository declarations;
clients URL-encode each segment. The response uses camel-case JSON. The
database field contains the reference and variable name only, never the secret
value.

| Method and suffix | Request | Successful response |
|---|---|---|
| `GET` at the base path | None | `200` with `database` (object or `null`), `volumes` (`name`, `mountPath`), `schedules`, `audit`, and `adapterReady`. `404` if the environment declaration is absent. |
| `PUT /{volume}/schedule` | `{ "hourUtc": 0..23 }` for a daily UTC hour, or `{ "hourUtc": null }` to disable | `200` with the updated schedule. |
| `POST /{volume}/backups` | Empty JSON object | `200` with `id`, `volume` (`project`, `environment`, `volume`), `createdAt`, `sha256`, and `bytes`. |
| `POST /{volume}/backups/{backupId}/restore` | Empty JSON object | `200` with no body after the staged volume replaces the live directory. |

A schedule has `project`, `environment`, `volume`, `hourUtc`, `lastAttemptAt`,
`lastError`, `pendingPrune`, and `backups` (the backup response shape above). An audit entry has
`id`, `at`, `actor`, `action`, `environment`, `volume`, `backupId`, and `outcome`.
The GET response selects schedules for the requested environment and its last
100 audit entries; the per-project store retains the last 500 entries. Restore
records a `started` entry before changing bytes, then `succeeded`, `failed`,
or `cancelled` when the result is known. Schedule changes, manual backups, and
missing backup IDs are also audited.

Mutations require a signed-in Studio session, its `X-CSRF-Token`, and a project
Owner or Maintainer; otherwise they return `403` with `{ "error": "..." }`.
An undeclared volume or an hour outside 0 to 23
returns `400`. Restore returns `404` if the backup ID is not registered for
that volume or its archive is absent, `409` if archive validation fails, and
`503` when the host adapter is unbound. Backup also returns `503` for an
unbound adapter. Other host or storage failures return `500`. The public demo
execution profile denies all three mutations. Read requests remain available so Studio can show the
declarations and `adapterReady` state.

Restore checks the archive checksum and paths before changing the live volume.
Once the staged directory replaces it, the API reports success even if removal
of the old directory fails; the host logs that cleanup failure and retains the
recovery marker. Another restore of that volume waits for the old directory to
be removed, so repeated restores cannot accumulate full copies. If the final audit write fails after
the replacement, the API still reports success and logs the audit error. The
earlier `started` entry remains as evidence of the attempt.
Before moving the live directory aside, the host writes a durable
`<volume>.restore-pending` marker that identifies its previous directory. On
adapter restart, a missing live directory is recovered from that previous
directory. If the staged replacement is already live, recovery keeps it and
retries cleanup of the previous directory before clearing the marker. An invalid marker or a missing live and
previous directory stops adapter startup for operator repair.

AGT-3229 was not on `origin/develop` when this work started. A control-plane
adapter implementing `IHostedVolumeBackupEngine` must call its run-zone host
agent for remote volumes. The host agent must mount `DeployJob.Data.Volumes`
at their declared paths and resolve
`DeployJob.Data.Database.SecretRef` through AGT-3222 using the claimed deploy
lease, then inject only its value as the declared environment variable. Until
that adapter is connected, Studio reports the host adapter as unavailable and
does not claim that a schedule produces a backup.
