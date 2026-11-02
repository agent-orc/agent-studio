# Studio website backup and restore

The networked website stack in `deploy/compose/monolith-networked` sends encrypted backups to a destination outside the host on a schedule. This page covers turning them on, what an archive holds, and the restore procedure. The [Studio website](./studio-website.md) page covers installing the stack.

## What a backup holds

| Data | In the archive | Comes back from |
|---|---|---|
| Workspace (`$STUDIO_DATA_DIR/workspace`): cards, `.metadata`, the workspace Git repository, `.security/access-state.json` (users, sessions, runner credentials) and the sealed secret store | Yes | The archive |
| Task storage under `$STUDIO_DATA_DIR/projects` outside checkouts, such as `.task-storage/<project>` | Yes | The archive |
| Registered task storage inside a project checkout | Yes | The archive |
| `refs/agent-studio/*` of each project checkout | Yes, as a Git bundle per checkout | The archive |
| Project checkouts (working tree, branches, history) | No; the archive records each checkout's `origin` URL and branch | The project remote |
| Checkouts without an `origin` remote | Yes, all refs as a Git bundle | The archive |
| Task worktrees | No | Their branches, through the checkout and the remote |
| Secret store key (`$STUDIO_DATA_DIR/keys/secrets.kek`) | No; the manifest records only its fingerprint | Its separate copy, see below |
| `home` (CLI logins), `caddy-data`, `caddy-config`, `origin-pull`, `.env` | No | Sign the CLIs in again, Caddy issues certificates again, the origin-pull CA comes from its owner copy, `setup.sh` writes a new `.env` |

URL passwords and tokens are removed from recorded `origin` URLs. The archive is encrypted with [age](https://age-encryption.org) to one or more public keys whose private keys are not on the host. It still holds credential material once decrypted, so treat a decrypted copy as secret.

## Turn backups on

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

### How a run works

`backup.sh` pauses the `api` container (`docker compose pause api`), so the files do not change while they are read and the archive is one consistent point in time. Requests wait during the pause; it lasts as long as reading, compressing and encrypting the data takes. Set `STUDIO_BACKUP_PAUSE_API=false` to skip the pause at the cost of a copy that can mix states from during the read. A one-shot container of the stack's own API image, running as UID 10001 without network, reads the data directories read-only through `backup-collect.sh` and writes a compressed tar stream into `age` on the host. The plaintext never touches the disk. The API is resumed before the upload. The archive and its checksum are uploaded under a temporary name and renamed once complete. Concurrent runs wait for each other.

The operator account needs Docker access, `age` and the stack's `.env`; the timer runs as root.

## Secret store key

The API seals secrets with the key-encryption key in `$STUDIO_DATA_DIR/keys/secrets.kek`, mounted read-only and named by `Secrets__KeyFile`. `setup.sh` creates it. It is deliberately not in the workspace, so no backup contains it, and a stolen archive together with the backup identity still does not open the sealed secrets.

Keep its own copy apart from the backups and the backup identity, for example as a second password manager entry:

```sh
bash secret-key.sh print "$STUDIO_DATA_DIR" > studio-secrets.kek   # move this file off the host, then delete it here
bash secret-key.sh id studio-secrets.kek                            # 16-character fingerprint
```

Each backup manifest records the fingerprint of the key that was in place, and restore refuses a key copy with a different fingerprint. Sealed secrets carried over from another installation open only with that installation's key; install its copy with `bash secret-key.sh install "$STUDIO_DATA_DIR" <key-copy>` before the first start. A stack installed before the key existed has no `keys` directory: run `bash secret-key.sh create "$STUDIO_DATA_DIR"`, then `docker compose up -d` to recreate the API with the mount, and make the copy.

## Restore

Restore needs the archive, the backup identity, and the secret store key copy. It writes into a fresh stack from `setup.sh` that has not been started, on the same or a replacement host.

1. On the target host, extract the matching release's `deploy/compose/monolith-networked` directory and run `bash setup.sh [--cloudflare | --private] <version> <hostname> <data-directory>` as for a new installation. Do not start the stack. Use the version that made the backup or a newer one.
2. Fetch the archive and its `.sha256` file from the backup destination with an account that may read it. Bring the backup identity and the secret store key copy to the host temporarily.
3. Make sure this account can clone each project remote: `restore.sh` clones the checkouts with this account's Git credentials (SSH agent, credential helper or `url.<base>.insteadOf` rules).
4. Run `bash restore.sh --identity <identity-file> --secret-key <key-copy> <archive>` in the stack directory. It checks the archive against its `.sha256`, reads the manifest, checks the key fingerprint, clones every checkout, and only then writes. It restores the workspace and task storage, places each clone at its recorded path, checks out its recorded branch, fetches the Studio refs from the bundle, and installs the key. It refuses a running stack and a stack whose workspace or projects directory is not empty.
5. Delete the identity and key copy from the host again.
6. For a public installation, install the origin-pull CA and the Docker firewall as in the [install steps](./studio-website.md#install). Point DNS at the host.
7. Run `docker compose up -d` (with `-f compose.yaml -f compose.runner-host.yaml` on a runner host) and sign in with an existing account. The installer code from the new `.env` is not needed, because the restored access state already has an owner.
8. Sign the CLIs in again, check that runners reconnect, and set the backup destination and timer on the new host before retiring the old one.

If a clone fails, `restore.sh` stops before it writes anything; fix the access and run it again. If a later step fails, run `docker compose down`, delete and recreate the data directory with `setup.sh`, and start over.

## Restore drill

`scripts/monolith-networked-backup-restore.test.sh` runs the whole cycle against throwaway stacks with their own Compose project names, subnet (`STUDIO_TEST_SUBNET_PREFIX`, default `172.30.184`) and loopback HTTPS port (`STUDIO_TEST_HTTPS_PORT`, default `18443`), so a website on the same host is not touched. It checks that a stack without a destination runs and reports backups as off. It then signs in, stores a sealed secret, adds a checkout with a Studio-only ref, a worktree and task storage, and backs up to a write-only directory. It checks that the archive is encrypted and holds neither the secret store key nor checkout content. It restores into an empty stack after refusing a wrong identity and a wrong key, refuses a second restore, and finally signs in, rotates the sealed secret and compares the Studio ref. CI runs it in the onboarding smoke workflow. Run it locally with Docker, `age`, `git`, `curl` and `python3`; set `WEBSITE_REUSE_WEBSITE_IMAGES=1` to use already built images of the current `VERSION`.

## Decisions

AGT-3365 implemented these options:

- **Scheduler:** a host systemd timer (`install-backup-timer.sh`) instead of a long-running backup container. The host already runs Docker and holds `.env`. Only the host can pause the API container without handing a container the Docker socket.
- **Reader:** a one-shot container of the stack's API image, which already has GNU tar, Git and Node, so the host needs no access to the UID 10001 data directories and no extra image.
- **Encryption:** age with public-key recipients. It streams, authenticates the ciphertext, and accepts several recipients, including SSH public keys.
- **Destinations:** SSH with `rrsync -wo`, or a mounted directory with write-only permissions. Both keep delete and read rights off the host. Object storage is reachable through a mounted gateway; a native S3 client is not part of the stack.
- **Projects:** checkouts come back from their remotes; only `refs/agent-studio/*` and task storage travel in the archive.
- **Secret store key:** a separate key file outside the workspace, mounted read-only, with a fingerprint check at restore.
