# Studio website backup and restore

The API of the networked website stack in `deploy/compose/monolith-networked` backs the whole installation up on a schedule, encrypts every archive, verifies the newest one by restoring it into an empty staging host, and copies each archive to a target outside the host (AGT-3574). This page covers what a backup holds, the keys the owner escrows, turning the off-host copy on, watching the state, and the restore onto a new host. The [Studio website](./studio-website.md) page covers installing the stack.

An older, host-level backup with a systemd timer and age encryption (AGT-3365) is still available as an optional second copy; it is described [at the end of this page](#host-level-backup-with-the-systemd-timer).

## What a backup holds

| Data | In the archive | Comes back from |
|---|---|---|
| Workspace (`/data/workspace`): cards, `.metadata` (registries, batch gate and engine state, management audit), the workspace Git repository, `.security` (users and IAM, sessions, API tokens, runner credentials) and the sealed secret store | Yes, as source `workspace`; Git repositories are staged with Git under the repository gate before their files enter the archive | The archive |
| Data home (`/data/home`): CLI logins, SSH keys and known hosts, `.local/share` (batch gate store and leases, gate cache, model routing, prompt overrides, project settings) | Yes, as source `home`, without package caches (`node_modules`, `.cache`, `.npm`, `.nuget`) and `cache` folders | The archive |
| Task storage under `/data/projects` outside checkouts, and registered task storage inside a checkout | Yes, as source `projects` | The archive |
| `refs/agent-studio/*` of each project checkout; all refs of a checkout without `origin` | Yes, as a Git bundle per checkout | The archive |
| Project checkouts (working tree, branches, history) | No; the manifest records each checkout's `origin` URL without credentials and its branch | The project remote |
| Task worktrees | No | Their branches, through the checkout and the remote |
| Host certificate state (`/data/host-certificates`) | Yes, as source `host-certificates` | The archive |
| Stack configuration: `.env`, Caddyfiles, `backup-target/` credentials | Yes, as source `stack`, without the `keys/` and `restored-stack/` directories | The archive, into `restored-stack/` for comparison |
| Caddy data (`caddy-data`: certificates and ACME account) and the origin-pull CA (`origin-pull`) | Yes, as sources `caddy-data` and `origin-pull` | The archive |
| Secret store key (`STUDIO_SECRETS_KEY_FILE`) | Never; the manifest records only its fingerprint | The owner's escrowed copy |
| Backup key (`keys/backup/backup.key`) | Never | The owner's escrowed copy |
| Host CA private and public keys (`STUDIO_HOST_CA_DIR`) | No | The owner's escrowed copy, installed with `restore-backup.sh --host-ca` |
| `api-tmp`, `caddy-config`, `frontend-data`, `frontend-config` | No | Recreated at start |
| Control plane lock (`workspace/.security/control-plane.lock`) | No; the running API holds it exclusively | Recreated at start |

With the archive, the backup and secret store keys, the escrowed host CA and the Git remotes, a new host comes back to the state of the backup. URL passwords and tokens are removed from recorded `origin` URLs. A file whose content is the secret store key or the backup key, or whose name ends in `.kek`, is left out of every source whatever its path; the archive's content list counts such files.

## Schedule, retention and verification

The API's `BackupHostedService` checks once a minute, five minutes after start:

- **Backup** every 6 hours (`Backup__IntervalHours`, `.env` `STUDIO_BACKUP_INTERVAL_HOURS`). A failed run is retried after 30 minutes.
- **Retention** after every backup: the newest 12 local archives stay (`Backup__RetentionCount`, `STUDIO_BACKUP_RETENTION`). Retention at the off-host target is configured there, because the host has no right to delete there.
- **Off-host copy** of every archive and its `.sha256` file before creation completes when a target is configured. A failed copy is reported as failed and retried after 15 minutes, doubling up to six hours.
- **Restore verification** of the newest good backup once a day (`Backup__VerifyIntervalHours`), and at once when the last verification failed and a newer backup exists. It checks the archive against the checksum recorded at creation, decrypts it, restores every source into an empty staging host below the backup directory with the real restore code, checks every file against the SHA-256 list in the archive, runs `git fsck --connectivity-only` on restored repositories, and deletes the staging host again.

Local archives live in `$STUDIO_DATA_DIR/backups` (`Backup__Directory`) as `studio-backup-<UTC time>-<id>.tar.gz.sbk` with a `.sha256` file and a non-secret `.json` record (size, hash, key fingerprints, consistency figures, verification and copy state). The backup directory must lie outside every source; the API refuses to back up otherwise.
Default machine-local state folders use distinct archive source names. A folder that resolves to a source already listed is skipped, and duplicate configured source names are rejected.

### Consistency

A backup never archives a half-written state:

- While the stores are read, the API holds ordinary HTTP mutations (cards, access, secrets, settings). Requests in flight finish first; new ones wait up to 60 seconds (`Backup__MaxRequestHoldSeconds`) and then get `503 backup-in-progress`. The management API, telemetry intake, runner lease and evidence writes and SignalR negotiation are not held; runner writes keep their deadlines and go through the per-file rules below. The hold ends after the read or after 120 seconds (`Backup__MaxWriteHoldSeconds`), whichever comes first; compression and encryption run during the read, the upload after it.
- SQLite databases (board summaries, telemetry, chat index) are copied with the SQLite online backup API, a transactionally consistent snapshot that folds the WAL in.
- JSON stores are replaced by atomic rename; each file is read through one handle and read again when its size or modification time changed during the read.
- Append-only journals (`.jsonl`, `.ndjson`, `.log`) are cut after their last complete line.

Background writers inside the API (runner loops, CLI logs) are not stopped; their files go through the same per-file rules. A file that changed on all five attempts is archived from its last complete read and listed as unsettled in the archive's content list and on the Operations page.

A file or directory that exists but cannot be opened for reading, for example because another process holds an exclusive lock on it, is left out; the backup goes on. The archive's content list names it with the reason, the API logs `backup-files-unreadable`, and the Operations page shows a warning while the newest good backup has such files. A failure to write the archive itself (backup directory not writable, disk full) fails the run with `backup-io-failed` and the path; `Create backup` then answers `409` with that reason instead of `500`.

## Keys

### Backup key

Every archive is encrypted with AES-256-GCM in 1 MiB authenticated chunks under a key derived per archive from the backup key (format `STUDIOBK` version 1). A changed, reordered, cut or extended archive, or a wrong key, fails authentication. The archive's plaintext never reaches the disk.

`setup.sh` creates the backup key at `keys/backup/backup.key` in the stack directory (`STUDIO_BACKUP_KEY_DIR`), outside the data directory, and the API mounts that folder read-only. The key is never in a backup, and the API refuses a key inside a source or next to the backups. For an installation set up before AGT-3574, create it and the backup folders with:

```sh
bash backup-key.sh create
```

Escrow it once, apart from the backups and apart from the secret store key copy, for example as its own password manager entry. Run the escrow commands as a host account with Docker access: the key belongs to UID 10001, and `print` reads it through a one-shot container of the edge image pinned by `STUDIO_EDGE_DIGEST`. The shell creates the output file before `print` runs, so a failed `print` (no Docker access, or a file that is not base64 of 32 bytes) says so and exits 1; the `|| rm -f` removes the empty file it leaves behind.

```sh
bash backup-key.sh print > studio-backup.key || rm -f studio-backup.key
```

Without the key file the Operations page shows `backup-key-missing` and no backup runs.

### Secret store key

The API seals secrets with the key-encryption key at `STUDIO_SECRETS_KEY_FILE`, mounted read-only and named by `Secrets__KeyFile`. `setup.sh` creates it outside the data directory, by default at `keys/secrets.kek` in the stack directory. No backup contains it in any form: a stolen archive together with the backup key still does not open the sealed secrets.

Escrow its own copy apart from the backups and apart from the backup key copy, for example as a second password manager entry, with the same Docker access as above:

```sh
bash secret-key.sh print "$STUDIO_DATA_DIR" > studio-secrets.kek || rm -f studio-secrets.kek
```

Every backup manifest records the fingerprint of the key that was in place (shown as the secret key id), and `restore-backup.sh` refuses a key copy with a different fingerprint. Sealed secrets carried over from another installation open only with that installation's key; the restore installs the copy at the path in `.env` before the first start. For an existing installation without `STUDIO_SECRETS_KEY_FILE`, follow the key setup instructions in [Studio website](./studio-website.md#update-and-stop) first.

Keep the two escrowed keys in separate places: whoever holds the backup key and an archive can read everything except the sealed secrets; only together with the secret store key do the sealed secrets open.

### Check the escrowed copies

This check is required, not optional: a copy counts as escrowed only when its id matches the Operations page. Before you move the two files off the host, and again on each copy in its safe place, run:

```sh
bash backup-key.sh id studio-backup.key    # must equal "Backup key id" on the Operations page
bash secret-key.sh id studio-secrets.kek   # must equal "Secret store key id" on the Operations page
```

Each command prints a 16-character fingerprint, or fails on a file that is not base64 of 32 bytes, such as an empty one. If an id is missing or differs, the copy is no escrow: delete it and repeat `print`. When both match, move the files off the host and delete them here.

## Off-host target

Set the target in `.env` and recreate the API container (`docker compose up -d api`). Credentials go into `backup-target/` in the stack directory, owned by UID 10001 with mode 600 (`bash backup-key.sh create` prepares the folder); the API mounts it read-only. Give the Studio host write access only: no read, list or delete, so a compromised host cannot remove the off-host copies.

| `STUDIO_BACKUP_TARGET` | Settings | Notes |
|---|---|---|
| `none` (default) | | Archives stay on the host; the Operations page warns `offhost-not-configured`. |
| `sftp` | `STUDIO_BACKUP_SFTP_HOST`, `STUDIO_BACKUP_SFTP_PORT` (22), `STUDIO_BACKUP_SFTP_USER`, `STUDIO_BACKUP_REMOTE_PATH`; `backup-target/ssh.key` and `backup-target/known_hosts` | OpenSSH `sftp` in batch mode with the pinned host key; there is no trust on first use. Uploads go to `<name>.partial` and are renamed. |
| `s3` | `STUDIO_BACKUP_S3_ENDPOINT` (`https://...`), `STUDIO_BACKUP_S3_REGION`, `STUDIO_BACKUP_S3_BUCKET`, `STUDIO_BACKUP_S3_PREFIX`, `STUDIO_BACKUP_S3_PATH_STYLE` (`true`); `backup-target/s3-access-key-id` and `backup-target/s3-secret-access-key` | Any S3-compatible store through one signed PUT (Signature Version 4), up to 5 GiB per archive. Upload time is bounded by archive size and response headers have a 30-second deadline after the body is sent. |
| `directory` | `STUDIO_BACKUP_MOUNT`, and start with `-f compose.yaml -f compose.backup-mount.yaml` | A mounted remote file system (NFS, SMB, sshfs) writable by UID 10001. A missing mount point fails the copy instead of writing locally. |

For SFTP, create the key with `ssh-keygen -t ed25519 -N '' -f backup-target/ssh.key`, verify the server's host key fingerprint out of band, pin it with `ssh-keyscan -p <port> <host> > backup-target/known_hosts`, and restrict the account on the server, for example with `ForceCommand internal-sftp -d /backups -P remove,rmdir,readdir` in a `Match User` block and a chroot. For S3, grant the access key `s3:PutObject` on the prefix only and enable bucket versioning or object lock, plus an expiry rule for retention. Alert at the target too when no new archive arrived for a day.

Check a new target with the management command `backup-upload` (below); it copies every archive that is not off-host yet.

## Watching backups

The **Operations** page of every project shows an **Installation backups** panel to owners and operators: the state, the last good backup with its time, age and size, the last restore verification, the off-host copy state and target, the schedule and the recent archives. It warns when:

| Code | Meaning |
|---|---|
| `backup-stale` | The newest good backup is older than 12 hours (`Backup__StaleAfterHours`, `STUDIO_BACKUP_STALE_AFTER_HOURS`). Also a `backup-stale` health reason in `GET /api/v1/management/status`. |
| `backup-missing` | No good backup exists yet. |
| `backup-failed` | The last run failed; the message carries the reason. Also a `backup-failed` health reason. |
| `backup-key-missing`, `backup-not-configured`, `backup-disabled` | No backup can run. |
| `verification-failed`, `verification-overdue` | The last restore verification failed, or none ran within two verification intervals. |
| `offhost-not-configured`, `offhost-invalid`, `offhost-pending`, `offhost-failed` | The newest backup is not, or not yet, off the host. |
| `host-ca-mismatch` | The configured host CA differs from the fingerprint in host certificate state, its public key is unreadable, or older issued state has no recorded fingerprint. Install the escrowed CA before issuing certificates. |

Owners and operators read the same state from `GET /api/v1/management/backups` (`no-store`). Operator agents, whose credentials never reach `/api/v1`, read it from `GET /api/operator/backups` or `studio-op backups`; a caller without installation-wide operate permission does not see the target or the key fingerprints, and host paths in its error text read `<path>`. Off-host errors and warning text in that scoped view contain no target detail. The management commands of `POST /api/v1/management/commands` run the same engine: `backup-create`, `restore-verify` (optional `backupId`), `backup-retention` (optional `retentionCount`) and `backup-upload`. Each needs `dryRun: false` and `confirmation` equal to the kind for a real run; `backup-create` no longer needs maintenance mode. A `backup-retention` run that keeps fewer archives than `Backup__RetentionCount` needs the Owner role; operators may preview it and may apply the configured count.

Before retiring or moving a host, run `backup-create` and wait until the newest backup shows **Copied** in the Installation backups panel. The command result reports whether that archive was copied, whether its copy failed, or whether no target is configured. If the copy failed, keep the host available and retry with `backup-upload`; a locally created archive is not yet safe from host loss.

### Backup audit

Every backup create, off-host copy, restore verification and retention, scheduled or manual, writes a record to the control plane audit chain (stream `backup`, operations `backup.create`, `backup.upload`, `backup.verify`, `backup.retention`) with the actor and the outcome `succeeded`, `failed` or `denied`. Scheduled runs show the actor `schedule`; manual commands the principal, and an operator session the session with the Owner who delegated it. The offline `decrypt` and `restore` tools record `backup.decrypt` and `backup.restore` with the actor `offline:<account>` (`restore-backup.sh` passes `offline:<user>@<host>`). The Owner reads the records under **Settings > Access > Backup activity** or from `GET /api/auth/audit/backups`. The management command ledger `workspace/.audit/management.jsonl` stays the idempotency record of the commands.

## Restore onto a new host

Restore needs the archive with its `.sha256` file, the backup key copy, the secret store key copy, the escrowed host CA private key and matching `.pub` file, and read access to the project remotes. It writes into a fresh stack from `setup.sh` that has not been started. Keep the host CA copy separate from data backups: certificates already enrolled on hosts trust this CA.

1. On the new host, extract the matching release's `deploy/compose/monolith-networked` directory and run `bash setup.sh [--cloudflare | --private] <version> <hostname> <data-directory>`. Do not start the stack. Use the version that made the backup or a newer one.
2. Fetch the archive and its `.sha256` file from the off-host target with an account that may read it. Bring both key copies and the escrowed host CA private key with its `.pub` file to the host temporarily.
3. Load an SSH agent with a key that can clone each project remote (`ssh-add`), or use HTTPS remotes that need no credentials.
4. Run `bash restore-backup.sh --key <backup-key-copy> --secret-key <secret-key-copy> --host-ca <escrowed-ca-private-key> <archive>` in the stack directory. Place the matching public key at `<escrowed-ca-private-key>.pub`. A one-shot container of the stack's API image checks the checksum and both key fingerprints, clones every checkout from its recorded origin and branch, and only then writes the workspace, home, task storage, host certificates, Caddy data and origin-pull CA, fetches the Studio refs from the bundles, and checks every file against its hash. It refuses a running stack and any target that already holds a file; the only exception is the workspace repository that `setup.sh` initialized (its single installer commit of `.gitignore` and a clean working tree), which the archived workspace and its repository replace. It installs both key copies and the host CA, checks the CA fingerprint against restored certificate state when recorded, and writes the archived stack configuration into `restored-stack/`.
5. Delete the temporary key and CA copies from the host again. Keep the installed CA in `STUDIO_HOST_CA_DIR`.
6. Compare `restored-stack/.env` with the new `.env`: carry over the backup target settings and `backup-target/` credentials; keep the new installer code, which is not needed because the restored access state already has an owner.
7. For a public installation, install the Docker firewall as in the [install steps](./studio-website.md#install). Point DNS at the host.
8. Run `docker compose up -d` (with `-f compose.yaml -f compose.runner-host.yaml` on a runner host) and sign in with an existing account. All browser sessions from the archive have been ended, so every person signs in again with their existing second factor. The first Owner sign-in shows the archive id and creation time with a security review notice. Check that runners reconnect, that the Operations page shows a new good backup after the first schedule tick, and that it reaches the off-host target, before retiring the old host.
9. Compare the archive creation time in that notice with the security changes made since then. In [Access](/#/workspace/settings/access), revoke or rotate **every** API or service account token and runner credential changed after the archive; reapply user disables, password resets, role reductions and other access changes. In [Secrets](/#/workspace/settings/secrets), rotate secrets changed after the archive and update their external copies. A token revoked after the backup can become valid again on the restored host until you revoke it again. Review the affected Access and Secrets records before retiring the old host.

If the archive does not open, a clone fails or a later step fails, the restore empties its targets again, puts the workspace repository from `setup.sh` back and stops. Fix the cause and run it again on the same data directory.

Backups made before the CA fingerprint was recorded can restore issued certificates without `host-certificates/ca-fingerprint`. The Operations page warns and issuance stays paused. After verifying that the restored `ca.pub` matches the `TrustedUserCAKeys` entry on every enrolled host, record its fingerprint as the API account:

```sh
ssh-keygen -lf "$STUDIO_HOST_CA_DIR/ca.pub" -E sha256 | awk '{print $2}' | sudo -u '#10001' tee "$STUDIO_DATA_DIR/host-certificates/ca-fingerprint" > /dev/null
```

Refresh the Operations page and confirm the warning clears before issuing certificates.

### Offline tools

The API binary has offline backup commands that run without starting the host, for example in a one-shot API container:

```sh
dotnet OrchestratorApi.dll --studio-backup inspect --key backup.key --archive <archive>         # manifest, file count, consistency
dotnet OrchestratorApi.dll --studio-backup decrypt --key backup.key --archive <archive> --output backup.tar.gz
dotnet OrchestratorApi.dll --studio-backup restore --key backup.key --archive <archive> --target workspace=<dir> ... [--no-clone]
dotnet OrchestratorApi.dll --studio-backup key-create <file> | key-id <file>
```

`decrypt` and `restore` take `--actor <name>` and `--audit-workspace <dir>`. A successful restore records into the restored `workspace` target, otherwise the record goes to `--audit-workspace` or to the configured `TaskRepository` when that exists. A failed restore never writes into one of its targets, which must stay empty for the next attempt; it then only prints that the run was not recorded.

A decrypted `tar.gz` holds credential material; delete it when done.

### Tested restore

`backend.Tests/StudioBackupTests.cs` restores a backup onto an empty fake host: a workspace with access state and an empty lane, a home with an owner-only SSH key and a package cache, a checkout with a remote, a Studio-only ref, task storage inside it, a worktree and an untracked file, a checkout without a remote, and task storage outside checkouts. The new host's data directory is laid out as `setup.sh` leaves it, including the initialized workspace repository; a variant refuses that workspace once it holds another file, a second commit or an edited `.gitignore`, and another keeps it in place when the archive is missing, the backup key is wrong or a clone fails. It checks the restored files and modes, the cloned checkout with its branch and Studio ref, that the worktree, the untracked file and the cache stay out, the installed secret store key, and that a second restore, a wrong secret store key and a wrong backup key are refused. The same class covers the schedule, retention, consistency while JSON, journal and SQLite stores are written, the key material guard, the off-host copy with fake, directory, SFTP and S3 targets, and the stale warning.

## Decisions

AGT-3574 implemented these options:

- **Scheduler:** a hosted service inside the API runs the existing management commands, so the schedule, its state and its warnings live in the product and need no host timer, and the API can hold its own writes. The host-level timer stays available as an optional second copy.
- **Consistency:** a bounded hold of HTTP mutations during the read, the SQLite online backup API, per-file stable reads and journal prefixes, instead of pausing the container, which only the host can do.
- **Encryption:** a symmetric backup key file outside the data directory, escrowed by the owner, with AES-256-GCM chunks. A key the API can read lets the product verify every day by a real restore; a public-key recipient would have made the daily restore verification impossible on the host.
- **Targets:** SFTP through the OpenSSH client in the image, S3-compatible storage through a signed PUT without a new package, and a mounted directory. All need only write access.
- **Backup audit (AGT-3737):** backup operations write to the existing hash-chained control plane audit trail instead of a second, unread ledger, and the Owner reads them in the Access area next to operator activity. Retention below the configured count needs the Owner; a preview stays open to operators, because it deletes nothing.
- **Coverage:** the data home, host certificates, stack configuration and Caddy data join the workspace and task storage; checkouts still come back from their remotes; the secret store key, the backup key and the host CA key stay with the owner.

## Host-level backup with the systemd timer

The AGT-3365 scripts back the data up from the host with age encryption to an SSH (`rrsync -wo`) or mounted destination. They pause the API container during the read. Use them as an optional second, independently keyed copy; the scheduled backups above do not need them.

### What a host-level backup holds

| Data | In the archive | Comes back from |
|---|---|---|
| Workspace (`$STUDIO_DATA_DIR/workspace`): cards, `.metadata`, the workspace Git repository, `.security/access-state.json` (users, sessions, runner credentials) and the sealed secret store | Yes | The archive |
| Task storage under `$STUDIO_DATA_DIR/projects` outside checkouts, such as `.task-storage/<project>` | Yes | The archive |
| Registered task storage inside a project checkout | Yes | The archive |
| `refs/agent-studio/*` of each project checkout | Yes, as a Git bundle per checkout | The archive |
| Project checkouts (working tree, branches, history) | No; the archive records each checkout's `origin` URL and branch | The project remote |
| Checkouts without an `origin` remote | Yes, all refs as a Git bundle | The archive |
| Task worktrees | No | Their branches, through the checkout and the remote |
| Secret store key (`STUDIO_SECRETS_KEY_FILE`, outside the data directory) | No; the manifest records only its fingerprint | Its separate copy, see below |
| `home` (CLI logins), `caddy-data`, `caddy-config`, `origin-pull`, `.env` | No | Sign the CLIs in again, Caddy issues certificates again, the origin-pull CA comes from its owner copy, `setup.sh` writes a new `.env` |

URL passwords and tokens are removed from recorded `origin` URLs. The archive is encrypted with [age](https://age-encryption.org) to one or more public keys whose private keys are not on the host. It still holds credential material once decrypted, so treat a decrypted copy as secret.

### Turn host-level backups on

1. Install `age` on the host (`apt install age`), and `rsync` with `openssh-client` for an SSH destination.
2. On a machine other than the Studio host, create the backup identity with `age-keygen -o studio-backup-identity.txt`. Store that file offline or in a password manager; it is the only way to read the backups. Put only its public line (`age-keygen -y studio-backup-identity.txt`, starting with `age1`) into `backup-recipients.txt` in the stack directory. One recipient per line; add a second operator's key for a second holder. `backup.sh` refuses a recipients file that contains a private key.
3. Keep a copy of the secret store key, described below.
4. Prepare a destination that this host can write to but not read, list or delete:
   - **SSH with rsync.** On the backup server, create an account and restrict the Studio host's key in its `~/.ssh/authorized_keys` to write-only rsync into one directory: `command="rrsync -wo /srv/studio-backups",restrict ssh-ed25519 AAAA... studio-backup`. `rrsync` ships with rsync 3.2.4 and later (on some distributions as the separate `rrsync` package). Create the key on the Studio host with `ssh-keygen -t ed25519 -N '' -f backup-ssh.key` in the stack directory. Verify the server's host key fingerprint out of band, then pin it with `ssh-keyscan -p <port> <host> > backup-known-hosts`. Set `STUDIO_BACKUP_DESTINATION=ssh://<user>@<host>[:<port>]`; a path after the host is relative to the `rrsync` directory.
   - **Mounted directory.** Mount a remote file system (NFS, SMB, sshfs) and give the Studio host an account that can create files in the backup directory but not read it, for example a directory of mode `0300` (or `1733` when it is shared) owned by that account, with server-side snapshots or object lock that the host cannot change. Set `STUDIO_BACKUP_DESTINATION=/mnt/studio-backups`.
5. Set the destination in `.env`. Optional settings: `STUDIO_BACKUP_RECIPIENTS` (recipients file, default `./backup-recipients.txt`), `STUDIO_BACKUP_SSH_KEY` (default `./backup-ssh.key`), `STUDIO_BACKUP_KNOWN_HOSTS` (default `./backup-known-hosts`), and `STUDIO_BACKUP_PAUSE_API` (default `true`).
6. Run `bash backup.sh` once and check that `studio-backup-<hostname>-<UTC time>.tar.gz.age` and its `.sha256` file arrived at the destination.
7. Install the schedule with `sudo bash install-backup-timer.sh`. The default runs daily at 03:15 with up to ten minutes of random delay; `--on-calendar '<spec>'` takes any systemd calendar expression, and `--remove` uninstalls the units. Check the last result with `bash backup.sh status`, the schedule with `systemctl list-timers agent-studio-backup.timer`, and the output with `journalctl -u agent-studio-backup.service`.

While `STUDIO_BACKUP_DESTINATION` is empty, the stack starts and runs normally. `setup.sh`, `backup.sh`, `backup.sh status` and every timer run report `Backups are off`, and the timer run ends successfully. Install the timer at setup anyway so the journal shows the off state until a destination is configured.

Retention lives at the destination, because the host has no right to delete there. Configure expiry or pruning on the backup server or storage, and alert there when no new archive arrived for more than a day.

#### How a host-level run works

`backup.sh` pauses the `api` container (`docker compose pause api`), so the files do not change while they are read and the archive is one consistent point in time. Requests wait during the pause; it lasts as long as reading, compressing and encrypting the data takes. Set `STUDIO_BACKUP_PAUSE_API=false` to skip the pause at the cost of a copy that can mix states from during the read. A one-shot container of the stack's own API image, running as UID 10001 without network, reads the data directories read-only through `backup-collect.sh` and writes a compressed tar stream into `age` on the host. The plaintext never touches the disk. The API is resumed before the upload. The archive and its checksum are uploaded under a temporary name and renamed once complete. Concurrent runs wait for each other.

The operator account needs Docker access, `age` and the stack's `.env`; the timer runs as root.

### Restore a host-level backup

Restore needs the archive, the backup identity, and the secret store key copy. It writes into a fresh stack from `setup.sh` that has not been started, on the same or a replacement host.

1. On the target host, extract the matching release's `deploy/compose/monolith-networked` directory and run `bash setup.sh [--cloudflare | --private] <version> <hostname> <data-directory>` as for a new installation. Do not start the stack. Use the version that made the backup or a newer one.
2. Fetch the archive and its `.sha256` file from the backup destination with an account that may read it. Bring the backup identity and the secret store key copy to the host temporarily.
3. Make sure this account can clone each project remote: `restore.sh` clones the checkouts with this account's Git credentials (SSH agent, credential helper or `url.<base>.insteadOf` rules).
4. Run `bash restore.sh --identity <identity-file> --secret-key <key-copy> <archive>` in the stack directory. It checks the archive against its `.sha256`, reads the manifest, checks the key fingerprint, clones every checkout, and only then writes. It restores the workspace and task storage, places each clone at its recorded path, checks out its recorded branch, fetches the Studio refs from the bundle, and installs the key. It refuses a running stack and a stack whose workspace holds more than the repository from `setup.sh` (its single installer commit of `.gitignore`) or whose projects directory is not empty.
5. Delete the identity and key copy from the host again.
6. For a public installation, install the origin-pull CA and the Docker firewall as in the [install steps](./studio-website.md#install). Point DNS at the host.
7. Run `docker compose up -d` (with `-f compose.yaml -f compose.runner-host.yaml` on a runner host) and sign in with an existing account. All archived browser sessions have been ended; the installer code from the new `.env` is not needed because the restored access state already has an owner. The first Owner sign-in shows the host-level archive filename and creation time in the security review notice.
8. Use that archive time to re-check [Access](/#/workspace/settings/access) and [Secrets](/#/workspace/settings/secrets). Revoke or rotate every token, runner credential and secret changed since the backup, and reapply user disables, password resets and role reductions. A token revoked after the backup may be valid again until re-revoked. Sign the CLIs in again, check that runners reconnect, and set the backup destination and timer on the new host before retiring the old one.

If a clone fails, `restore.sh` stops before it writes anything; fix the access and run it again. If unpacking fails, it empties the workspace and projects directory, puts the workspace repository from `setup.sh` back, and stops; fix the cause and run it again.

### Host-level restore drill

`scripts/monolith-networked-backup-restore.test.sh` runs the whole cycle against throwaway stacks with their own Compose project names, subnet (`STUDIO_TEST_SUBNET_PREFIX`, default `172.30.184`) and loopback HTTPS port (`STUDIO_TEST_HTTPS_PORT`, default `18443`), so a website on the same host is not touched. It checks that a stack without a destination runs and reports backups as off. It then signs in, stores a sealed secret, adds a checkout with a Studio-only ref, a worktree and task storage, and backs up to a write-only directory. It checks that the archive is encrypted and holds neither the secret store key nor checkout content. It restores into an empty stack after refusing a wrong identity and a wrong key, refuses a second restore, and finally signs in, rotates the sealed secret and compares the Studio ref. CI runs it in the onboarding smoke workflow. Run it locally with Docker, `age`, `git`, `curl` and `python3`; set `WEBSITE_REUSE_WEBSITE_IMAGES=1` to use already built images of the current `VERSION`.

### Host-level decisions

AGT-3365 implemented these options:

- **Scheduler:** a host systemd timer (`install-backup-timer.sh`) instead of a long-running backup container. The host already runs Docker and holds `.env`. Only the host can pause the API container without handing a container the Docker socket.
- **Reader:** a one-shot container of the stack's API image, which already has GNU tar, Git and Node, so the host needs no access to the UID 10001 data directories and no extra image.
- **Encryption:** age with public-key recipients. It streams, authenticates the ciphertext, and accepts several recipients, including SSH public keys.
- **Destinations:** SSH with `rrsync -wo`, or a mounted directory with write-only permissions. Both keep delete and read rights off the host. Object storage is reachable through a mounted gateway; a native S3 client is not part of the stack.
- **Projects:** checkouts come back from their remotes; only `refs/agent-studio/*` and task storage travel in the archive.
- **Secret store key:** a separate key file outside the workspace, mounted read-only, with a fingerprint check at restore.
