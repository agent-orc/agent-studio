# Docker operations

The root [`docker-compose.yml`](../../../docker-compose.yml) is the one-box
product deployment. Begin with [Getting started](./getting-started.md).

## Configuration and network access

Copy `.env.example` to `.env`, then set `AGENT_STUDIO_VERSION` to a published
release tag. By default the UI, Task Server, BFF, API bridge, and optional TLS
edge publish only on `127.0.0.1`. Set `STUDIO_UI_BIND_ADDRESS` to a specific
private interface address to admit LAN clients. Do not publish the Task Server
port to a LAN; its principal API is exposed through the authenticated BFF.
The legacy route bridge has no LAN session boundary, so use a trusted private
network or tunnel for this option C deployment.
Set `STUDIO_EDGE_BIND_ADDRESS` and `STUDIO_EDGE_HOSTNAME` before starting the
`edge` profile. Caddy issues a local certificate with its internal CA and
retains CA material in `edge-data` and `edge-config`. Browsers on other devices
must trust that CA through an operator-managed channel.

```sh
docker compose --profile edge up -d --wait
```

The `core` network lets the Runner reach Git and CLI providers. Internal
services have no public port other than the explicit loopback bindings.

## Volumes and platform behavior

`orchestrator-data`, `backup`, `credentials`, `workspace`, `projects`, and the runner
workspace volumes are Docker named volumes. They are retained by
`docker compose down`. Do not use `down --volumes` for a real installation.
On Docker Desktop, named volumes live inside the Linux VM, not under the
Windows or macOS checkout. The optional runner credential directory mounts
are host bind mounts; on Windows, keep the checkout on a WSL2 Linux
filesystem for predictable permissions and performance. The UI loopback
binding is on the Docker host; a second machine cannot reach it until the
bind address is explicitly changed or a private tunnel is used.

## Runner credentials

The default `agent-host` has a generated Task Server principal and registers
without an operator secret. Before executing tasks, set `RUNNER_GIT_REMOTE`
and optionally `RUNNER_GIT_PUSH_REMOTE` in `.env`. Place CLI login files under
`.runner-credentials/claude`, `.runner-credentials/codex`, or
`.runner-credentials/gemini` and SSH material under `.runner-git`. These mounts
are read-only in the containers. The directory roots can be changed with
`RUNNER_CREDENTIALS_DIR` and `RUNNER_GIT_CREDENTIALS_DIR`. Credential and Git
setup depends on the provider and repository; the stack does not invent those
external credentials. `RUNNER_CLI_BIN` can select a CLI already in the image.

```sh
docker compose --profile runner up -d --wait
```

This adds `agent-host-coding` and `agent-host-review`, each with a distinct
principal and workspace volume. Their generated credentials remain in the
same private `credentials` volume. Rotate an internal principal with one
product command, for example `scripts/compose-rotate-credential.sh runner`.
Accepted names are `studio`, `engine`, `runner`, `coding`, and `review`. The
command calls the Task Server management API, writes the returned credential
to the volume with mode `0600`, and restarts the matching client. It never
prints the credential. AGT-W49's Operations Server is the recommended later
home for this host-management action.

## Update and backup

Pin the new release tag in `.env`, then run:

```sh
docker compose pull
docker compose up -d --wait
```

The `orchestrator-data`, `backup`, and `credentials` volumes survive container replacement.
Take a full backup before a version change:

```sh
docker compose exec task-server dotnet task-server.dll backup full
```

The complete set is written to the `backup` volume. Copy that volume to
another host or storage system for off-host durability; a named volume on the
same machine does not protect against host failure. Verify and restore with
the Task Server's `backup verify-full <id>` and `backup restore-full <id>`
commands. For a restore after putting the required backup set into the
`backup` volume, stop the stack, verify, restore, then start it again:

```sh
docker compose down
docker compose run --rm --no-deps --entrypoint dotnet task-server task-server.dll backup verify-full <id>
docker compose run --rm --no-deps --entrypoint dotnet task-server task-server.dll backup restore-full <id>
docker compose up -d --wait
```

Back up and restore the matching `credentials` volume with the store. Its
files are mode `0600` and must remain private in any exported archive.
See [Task Server full backup sets](./task-server.md#full-backup-sets) for exact
restore conditions and version compatibility. The bootstrap refuses a store
whose principal files are missing.

## Diagnostics and sizing

```sh
docker compose ps
docker compose logs --tail 200 task-server orchestrator-engine studio-bff agent-host
docker compose exec task-server curl -fsS http://127.0.0.1:5071/healthz
```

Task Server, BFF, API bridge, and web have 1 GB memory and two CPU limits per
container; the runner has 4 GB and four CPUs; the TLS edge has 256 MB. Adjust
these limits to match the number and size of concurrent tasks. Leave disk
headroom for runner worktrees, backup sets, and image replacement.
