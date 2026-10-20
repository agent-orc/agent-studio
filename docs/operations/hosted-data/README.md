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
directory. The app must be quiescent for a consistent backup and restore.

AGT-3229 was not on `origin/develop` when this work started. A control-plane
adapter implementing `IHostedVolumeBackupEngine` must call its run-zone host
agent for remote volumes. The host agent must mount `DeployJob.Data.Volumes`
at their declared paths and resolve
`DeployJob.Data.Database.SecretRef` through AGT-3222 using the claimed deploy
lease, then inject only its value as the declared environment variable. Until
that adapter is connected, Studio reports the host adapter as unavailable and
does not claim that a schedule produces a backup.
