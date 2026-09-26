# Docker operations

## Install from this checkout

Docker Engine with Compose v2, or Docker Desktop, is required. Allow at least
8 GB of disk space for images and builds, plus space for task data and runner
workspaces. From the repository root:

```sh
docker compose --profile dev up --build --wait task-server-dev orchestrator-engine-dev studio-bff-dev orchestrator-api-dev web-dev agent-host-distributed-dev
```

Open `http://localhost:4011`. This source-built path is the verified default
for the current checkout. To run it in the background, add `-d` before `--wait`.
The Task Server, Engine, BFF, Studio API proxy, web UI, and one agent host start
together. The Studio API proxy preserves the `/api/v1` distributed route path.
The remaining dev-seat routes have the option C coverage limit described in
[the connector gap](./docker-compose-connector-gap.md).

## Published images

After release CI has checked the published images, pin a release and start the
same stack without a source build:

```sh
cp .env.example .env
# Set AGENT_STUDIO_VERSION in .env to the published release number, without v.
docker compose up -d --wait
```

The root file uses `ghcr.io/agent-orc` images tagged `v<AGENT_STUDIO_VERSION>`.
Only services under the `dev` profile contain `build:`. The published-image
path is verified by the post-release smoke run, separate from this checkout's
source-build verification.

## Bootstrap and credentials

A one-shot `bootstrap` service creates three independent random 256-bit bearer
values in the `agent-studio_secrets` named volume. It sets each file to mode
`0600` and ownership to service uid 10001. The Task Server reads the files to
create Studio, Engine, and Runner principals on an empty store. Other services
read the same files through read-only mounts. Subsequent `up` runs leave the
files untouched. Do not edit or remove individual files from the secrets
volume: principal credential rotation belongs to the product's Operations
Server or host manager workflow when available. `docker compose down` retains
the volume; `down --volumes` deletes it and all installation data.

The included agent host registers without a Git remote or CLI login. To run
coding tasks, set `RUNNER_GIT_REMOTE` and `RUNNER_GIT_PUSH_REMOTE` in `.env`.
Mount your provider credentials using `RUNNER_CLAUDE_CREDENTIALS_DIR`,
`RUNNER_CODEX_CREDENTIALS_DIR`, or `RUNNER_GEMINI_CREDENTIALS_DIR`; each maps to
the corresponding directory under `/home/runner`. For Git over SSH, use
`RUNNER_SSH_CREDENTIALS_DIR`; for HTTPS credential-store, use
`RUNNER_GIT_CREDENTIALS_FILE`. These host paths remain outside images and the
store. On Docker Desktop, use absolute host paths shared with the Linux VM.
Windows paths pass through WSL2's file sharing and can have different ownership
semantics from named Linux volumes. Keep the default named volumes for the
store, backup, secrets, and runner workspaces on all hosts.
On Linux, make mounted provider directories readable and writable by container
uid 10001 so the CLIs can refresh their own session files. Keep Git credential
mounts outside the repository build context.

## Network and edge

The UI binds to `127.0.0.1:4011` by default. Set `STUDIO_UI_BIND=0.0.0.0`
explicitly for LAN access. Task Server always publishes only to host loopback;
container communication uses the Compose `control` network. The optional
`edge` profile starts Caddy with a persistent local certificate authority:

```sh
docker compose --profile edge up -d --wait
```

Set `STUDIO_EDGE_HOSTNAME`, `STUDIO_EDGE_BIND`, and `STUDIO_EDGE_PORT` in `.env`
for the intended private-network name and listener. Clients must trust the
Caddy local CA, or use an existing trusted TLS terminator.

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
docker compose logs --tail 200 task-server orchestrator-engine orchestrator-api web agent-host-distributed
```

Use `docker compose down` to remove containers while retaining named volumes.
Sizing starts at 2 CPUs and 2 GB for each API service, 4 CPUs and 4 GB for the
runner, and 8 GB free disk for images and build layers. Adjust the per-service
Compose limits after measuring your workloads.
