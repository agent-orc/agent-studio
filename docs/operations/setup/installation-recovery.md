# Installation recovery set and empty-target drill

This runbook implements Dossier AGT-W63 decision D7, option A, delivered as
item I07 ([deployment story](../../deployment-story/index.html#recovery)). An
installation is recoverable only from a verified full recovery set that has
been rehearsed onto an empty target. A retained Docker volume, a successful
database backup alone, or a workspace Git push is not a verified full backup.

The recovery set reuses the Task Server's existing full backup set (see
[task-server.md, "Full backup sets"](./task-server.md#full-backup-sets)) and
restore contract (see
[task-server.md, "Backup and restore rehearsal"](./task-server.md#backup-and-restore-rehearsal)).
It adds a versioned manifest, an off-host copy receipt, a guided verification
with specific recovery guidance, and a resume gate. All store changes go
through Task Server services. Nothing here edits managed workspace project or
metadata folders directly.

## What the set contains

A copied recovery set is one directory per backup id:

| Member | Content |
|---|---|
| `set/` | The unchanged full backup set: `snapshot.db`, `manifests/`, referenced `cold/` payloads, `export/`, `inventory.json` (per-file SHA-256) and, written last, `complete.json`. |
| `recovery-manifest.json` | Schema `agent-studio.recovery-manifest/v1`, described below. |
| `custody.json` | The administrator's custody declaration as captured. It holds references only, never secret values. |
| `copy-receipt.json` | Copy time, destination, set digest, manifest digest, file count and warnings. It is written only after the copy has been verified. |

The manifest records:

- **Authoritative store.** The store type (`task-server-sqlite`), the exact
  release and Git SHA that wrote the set, and the store schema version.
- **Identities.** The server id, workspaces, projects (id, key prefix, task
  count), the task count, and a digest over every task id, key and project.
- **Data set.** The backup id, set digest, completion marker, inventory, file
  count and size.
- **Cold evidence.** Every referenced cold payload with its digest.
- **External artifacts.** Pointer-only artifacts that the authority does not
  store.
- **Repositories.** Each origin and its sampled canonical refs. These are
  accepted result refs from result handoffs, plus declared refs that are
  resolved against the origin at capture time.
- **Configuration.** References to installation configuration files, with
  digests where the file is readable.
- **Secret custody.** The location, digest, encryption and holder of the
  separately encrypted secret bundle, and a custody for every active principal:
  `secret-bundle`, `re-enrol` or `undeclared`. Principal hashes in the database
  cannot reconstruct a client's cleartext credential.
- **Pending host obligations.** Runner outboxes with unacknowledged records,
  and accepted results that exist only as a source bundle (salvage). These are
  never cache.
- **Rebuildable caches.** Runner worktrees, preflight, token and metrics
  caches, and container images. The drill does not restore them.

### Custody declaration

The administrator keeps one custody file outside the store, for example
`/etc/agent-orchestrator/recovery-custody.json`:

```json
{
  "installationId": "inst_<from the installation manifest>",
  "configuration": [
    {"name": "compose environment", "location": "/etc/agent-orchestrator/docker.env", "custody": "secret-bundle"}
  ],
  "secretBundle": {
    "location": "/mnt/agent-orchestrator-offhost-backup/secrets/installation.age",
    "encryption": "age",
    "recoveryCredentialHolder": "administrator"
  },
  "clients": [
    {"principalId": "runner:host-a", "custody": "secret-bundle"},
    {"principalId": "runner:host-b", "custody": "re-enrol"}
  ],
  "repositories": [
    {"repositoryId": "<repository id>", "origin": "git@example:org/repo.git", "refs": ["refs/heads/main"]}
  ]
}
```

Create and encrypt the secret bundle with your own tooling, and keep its
decryption key with the recovery credential holder. The Task Server reads only
its digest.

## Workflow

Every command opens the store directly. Stop the serving process first, or
take the capture from the scheduled backup path, so that the command is the
only writer. Pass the same store settings as the service (`STORE_PATH`,
`BACKUP_PATH`, `ARCHIVE_PATH` or the `--TaskServer:*` options).

1. **Capture.** `task-server recovery capture --custody recovery-custody.json`.
   This first resolves the declared refs against each origin; an unreachable
   origin stops the capture before any set is written. It then creates a full
   backup set through the existing service and writes
   `<backups>/recovery/<backup-id>.recovery-manifest.json`.
2. **Copy off-host.** `task-server recovery copy --backup <id> --to <mounted off-host path>`.
   The destination must be empty. The copy is re-inventoried before
   `copy-receipt.json` is written. A destination inside the authority's own
   data or backup directory is flagged in the receipt. A named volume on the
   same disk is not off-host recovery.
3. **Verify.** `task-server recovery verify --from <copy>/<id> [--secret-bundle <path>]`.
   This needs no store. It checks the manifest schema, the completion marker,
   every inventory hash, the cold payloads, schema compatibility, the sampled
   Git refs, the secret bundle digest and client credential
   custody. Exit code 2 means the copy must not be restored. The Task Server
   never starts Git. It reads local bare repositories from their ref files and
   HTTP(S) origins through the smart-HTTP ref advertisement. For SSH origins,
   pass `--git-refs origins.json`, which maps each origin to a file holding
   `git ls-remote <origin>` output that the operator ran.
4. **Restore to an empty target.** Use the same release:
   `STORE_PATH=<empty> task-server recovery restore --from <copy>/<id> --loss-at <UTC loss instant>`.
   The command refuses non-empty data, backup or archive directories. It
   verifies first, then uses the full backup restore service, and compares the
   server id, schema, task count, task identity digest, workspaces, projects
   and every cold payload digest. It writes `recovery-restore-receipt.json`
   in the data directory. The target stays in `Maintenance`.
5. **Fence stale hosts.** `task-server recovery fence-hosts` revokes every
   runner credential issued before the restore. A host still holding the old
   authority's credential is rejected at authentication.
6. **Re-enrol one host deliberately.**
   `task-server recovery reenrol --principal runner:<id> --credential-out <file>`
   writes one fresh credential to an owner-only file. Deliver it to that host's
   protected token file.
7. **Resume.** Run `task-server recovery resume --check-only` until no blocker
   remains, then
   `task-server recovery resume --old-writer-closed [--obligations-retained]`.
   The gate requires all of the following:
   - a recovery restore receipt;
   - `Maintenance` mode;
   - no `active` or `process-unknown` attempt;
   - the attestation that the previous authority is stopped (one writer);
   - no unfenced pre-restore runner credential;
   - no open set finding (such as unverified Git refs or a missing secret
     bundle);
   - for each pending host obligation, either reconciliation or the
     attestation that the host's outbox and salvage are kept.
8. **Reconnect and canary.** Start the service. Reconnect the re-enrolled host
   with a new instance id; its old run leases are not accepted. Run one canary
   task, then reopen admission for further hosts one at a time.

## Failure guidance

| Code | Effect | What to do |
|---|---|---|
| `incomplete-set` | Restore refused | The set stopped before `complete.json`. Never write the marker by hand. Use the newest earlier verified copy, or capture again. |
| `corrupted-hash` | Restore refused | Discard the copy and fetch another off-host copy whose receipt shows the same set digest. Never edit `inventory.json`. |
| `missing-cold-payload` | Restore refused | Copy the payload from another verified copy with the same set digest, or capture again while the source archive still holds it. |
| `schema-mismatch` | Restore refused | Install the release named in the guidance on the empty target, restore, verify, and only then upgrade. |
| `manifest-missing` / `manifest-unsupported` | Restore refused | Copy again with `recovery copy`, or use the release that captured the set. |
| `git-origin-unavailable` | Resume blocked | Restore network access or the origin credential, or declare a verified mirror. Stay in `Maintenance` until the refs verify. |
| `git-ref-missing` / `git-origin-undeclared` | Resume blocked | Publish the recorded commit from host salvage, or declare the origin, then verify again. |
| `client-credentials-lost` / `secret-bundle-missing` / `secret-bundle-changed` | Resume blocked | Fetch the bundle copied with this set. Otherwise fence hosts, then re-enrol each affected host and redeliver its credential. |
| `pending-host-obligation` | Resume blocked until reconciled or attested | Keep that host's outbox and worktree. It drains against the restored authority under fencing after its reconnect. |
| `release-differs`, `git-ref-moved`, `copy-receipt-missing` | Advisory | Recorded in the receipt. |

## Drill and measured objectives

`scripts/recovery-drill.sh [report.json]` runs the rehearsal on one disposable
host under a fresh temporary root. It seeds a source authority through the
HTTP API, captures and copies the set, writes once more after capture, and
records the loss instant. It then restores to an empty data directory, fences
hosts, passes the resume gate, serves the restored store and creates a canary
task. The report gives the measured recovery point (loss instant minus capture
time), the measured recovery time to the canary, the identity comparisons and
the tasks lost after capture. `RecoveryDrillTests` in `task-server.Tests` runs
the full sequence in process. It covers cold evidence, a bare Git origin with
sampled refs, credential fencing, obsolete-replay rejection, re-enrolment and
a canary lease. It also injects each fault in the table above, and asserts
that production state is unchanged and that a refused restore leaves the
target empty.

Report recovery objectives only from such measurements. The 300-second backup
timer in [control-plane-docker.md](./control-plane-docker.md#backup-and-restore)
is a capture interval, not an achieved recovery point. The worst-case recovery
point is that interval plus the age of the newest verified off-host copy at
the time of the loss.

## Limits

- Capture covers the standalone Task Server SQLite authority. A legacy
  file-tree workspace keeps its own `retention backup-full`, `verify-full` and
  `restore-full` set (Git bundle, untracked evidence, cold payloads,
  inventory, `complete.json`). Move it to the Task Server through the legacy
  migration before relying on this drill. The manifest reserves the
  `file-tree-workspace` store type for that set.
- The installation id is copied from the custody declaration until the I06
  installation manifest owns it.
- Runner-side outbox contents live on the runner host. The manifest records
  the authority's view of each pending obligation; the host keeps the data.
