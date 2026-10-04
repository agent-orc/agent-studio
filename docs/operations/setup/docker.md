# Docker operations

## Install from this checkout

Docker Engine with Compose v2, or Docker Desktop, is required. Allow at least
8 GB of disk space for images and builds, plus space for task data and runner
workspaces. From the repository root:

```sh
scripts/compose-distributed-bootstrap.sh
docker compose --profile dev up --build --wait task-server-dev orchestrator-engine-dev studio-bff-dev orchestrator-api-dev web-dev agent-host-distributed-dev agent-host-review-distributed-dev
```

Open `http://localhost:4011`. This source-built path is the verified default
for the current checkout. To run it in the background, add `-d` before `--wait`.
The Task Server, Engine, BFF, Studio API, web UI, and coding and review hosts start
together. Browser `/api/v1` requests go through the BFF to the Task Server.
The remaining dev-seat routes have the option C coverage limit described in
[the connector gap](./docker-compose-connector-gap.md).
The compatibility API rejects non-versioned task routes and has no durable
workspace or project mount. Browser mutations reach the Task Server through
the BFF on `/api/v1`.

## Published images

After release CI has checked the published images, pin a release and start the
same stack without a source build:

```sh
scripts/compose-distributed-bootstrap.sh
# Set AGENT_STUDIO_VERSION in .env to the published release number, without v.
docker compose up -d --wait
```

The root file uses `ghcr.io/agent-orc` images tagged `v<AGENT_STUDIO_VERSION>`.
Only services under the `dev` profile contain `build:`. The published-image
path is verified by the Release workflow's `Smoke published one-box images`
step, after `Publish pinned release images`, separate from this checkout's
source-build verification. That job needs GHCR read access and the complete
compatible image set containing the installation changes.

N-1 upgrade evidence needs both the prior and current compatible image sets
published under `v<version>`, plus a Docker job that can pull them and run
`scripts/compose-upgrade-test.sh` with `PREVIOUS_VERSION` and
`CURRENT_VERSION`. This script is a release follow-up; the current Release
workflow does not invoke it. A source build cannot supply that evidence. Supported
desktop and VM placement still needs clean Windows/macOS Docker Desktop and
Ubuntu VM jobs with provider login and Git fetch/push credentials.

## Bootstrap and credentials

The host bootstrap creates owner-only `.env` and `runner.env`, preserving
existing contents on repeated runs. The retained `compose-runner-bootstrap.sh`
command invokes the same installation path. Both role services optionally read
`runner.env` for provider environment credentials and slot limits. Git remotes
and credential mount paths belong in `.env`; Compose service values take
precedence over `runner.env`. No unused `runner.token` is created.

A one-shot `bootstrap` service creates four independent random 256-bit bearer
values in separate subdirectories of the `agent-studio_secrets` named volume.
It sets each file to mode `0600` and ownership to service uid 10001. The Task
Server reads the four files to create Studio, Engine, coding Runner, and review
Runner principals on an empty store. Engine, Studio BFF/API, and each runner
role mount only their own subdirectory read-only. Bootstrap migrates existing
root-level files into those directories without changing their values and
keeps compatibility links inside the bootstrap volume. Subsequent `up` runs
leave the files untouched. Rotate a principal with the included host-manager
command:

```sh
scripts/compose-rotate.sh runner --dev
scripts/compose-rotate.sh review-runner --dev
scripts/compose-rotate.sh engine --dev
scripts/compose-rotate.sh studio --dev
```

For a published-image stack, omit `--dev`. The command calls the Task Server
principal API, replaces the protected file in the named volume, and recreates
the matching service within the credential overlap. Run it while coding tasks
are idle because rotating the Runner recreates its container. It never prints
or requires pasting a bearer value. Do not edit or remove individual files
from the volume. `docker compose down` retains the volume; `down --volumes`
deletes it and all installation data.

The included coding and review hosts register without a Git remote or CLI
login. To run coding tasks, set `RUNNER_GIT_REMOTE` and
`RUNNER_GIT_PUSH_REMOTE` in `.env`. Each role defaults to its own writable
native CLI login volumes under `/home/runner`, with service uid 10001 and a
`0700` root. `runner.env` remains the provider environment source if selected.
Task Server does not mount either provider source. For an operator-managed
host bind, set `RUNNER_CLAUDE_CREDENTIALS_DIR`,
`RUNNER_CODEX_CREDENTIALS_DIR`, or `RUNNER_GEMINI_CREDENTIALS_DIR` for Coding.
Use the corresponding `REVIEW_RUNNER_` variables for Review. Each role's
override is independent. For Git over SSH, use the role's
`RUNNER_SSH_CREDENTIALS_DIR`; for HTTPS credential-store, use the role's
`RUNNER_GIT_CREDENTIALS_FILE`. These host paths remain outside images and the
store. On Docker Desktop, use absolute host paths shared with the Linux VM.
Windows paths pass through WSL2's file sharing and can have different ownership
semantics from named Linux volumes. Keep the default named volumes for the
store, backup, principal secrets, runner workspaces, and native CLI stores on
all hosts.
On Linux, make mounted provider directories readable and writable by container
uid 10001 so the CLIs can refresh their own session files. Keep Git credential
mounts outside the repository build context.

## Network and edge

The HTTP UI binds to `127.0.0.1:4011`. Task Server and BFF diagnostics
also publish only to host loopback;
container communication uses the Compose `control` network. The optional
`edge` profile starts Caddy with a persistent local certificate authority:

```sh
docker compose --profile edge up -d --wait
```

Set `STUDIO_EDGE_HOSTNAME`, `STUDIO_EDGE_BIND`, and `STUDIO_EDGE_PORT` in `.env`
for the intended private-network name and listener. Clients must trust the
Caddy local CA, or use an existing trusted TLS terminator. Set
`STUDIO_ALLOWED_ORIGINS` to include the exact `https://<host>:<port>` browser
origin before enabling a LAN listener. Foreign origins and requests without
an Origin on browser mutations return 403. The Connector's fixed loopback
authority and origin remain unchanged.

## Update, backup, restore, and logs

For a source-built update, pull the desired repository revision and repeat the
source-build command. For a published update, change only
`AGENT_STUDIO_VERSION` in `.env`, then run:

```sh
docker compose pull
docker compose up -d --wait
```

Take a full Task Server backup before an update. The backup set is written to
the `agent-studio_backup` named volume:

```sh
docker compose exec -T task-server dotnet task-server.dll backup full --json
```

For a source-built stack, replace `task-server` with `task-server-dev`. Record
the returned backup ID. Verify a backup with `backup verify-full <backup-id>`.
A full restore requires maintenance mode and a stopped writer; follow the
[Task Server backup procedure](./task-server.md) for the maintenance sequence,
then run `backup restore-full <backup-id>` in the Task Server container. Keep an
off-host copy of the backup volume for host-loss recovery.

Inspect health and bounded service logs with:

```sh
docker compose ps
docker compose logs --tail 200 task-server orchestrator-engine orchestrator-api web agent-host-distributed agent-host-review-distributed
```

Use `docker compose down` to remove containers while retaining named volumes.
Sizing starts at 2 CPUs and 2 GB for each API service, 4 CPUs and 4 GB for the
runner, and 8 GB free disk for images and build layers. Adjust the per-service
Compose limits after measuring your workloads.
