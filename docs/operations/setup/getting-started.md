# Getting started

Docker Compose is the primary installation path for Agent Studio on Linux,
Windows with Docker Desktop and WSL2, and macOS with Docker Desktop. The same
root `docker-compose.yml` starts the distributed Task Server, Orchestrator
Engine, Studio BFF, legacy route bridge, web UI, and a registered Agent Host.

## Prerequisites

Install Docker Engine with Compose v2 or Docker Desktop. Allow at least 8 GB of
free disk space for images and task data. The host needs neither .NET nor Node.js.

## Install

```sh
git clone https://github.com/agent-orc/agent-studio.git && cd agent-studio
cp .env.example .env
docker compose up -d --wait
```

Open [http://localhost:4011](http://localhost:4011). The UI binds to
`127.0.0.1` by default. All product containers use the same published image
tag from `AGENT_STUDIO_VERSION`. Set an exact released tag in `.env` before
starting if you need a reproducible installation.

The one-shot `bootstrap` container creates five 256-bit principal credentials
in the named `credentials` volume on the first start. Files are mode `0600`
and owned by the service UID. The Task Server creates the matching Studio,
Engine, and Runner principals; no token needs to be generated or pasted.
Later `up` runs reuse the files. Preserve the `credentials` volume alongside
the `orchestrator-data` volume. The bootstrap refuses to mint new Studio,
Engine, or Runner secrets for an existing store whose secret volume is missing.
An older distributed install with `DISTRIBUTED_*_TOKEN` entries in `.env` is
migrated into the credentials volume automatically.

The default Agent Host registers immediately. To run coding tasks it also
needs a CLI login and Git access, both external credentials. See
[Docker operations](./docker.md#runner-credentials) for their read-only mounts.
Additional coding and review containers are available with
`docker compose --profile runner up -d --wait`.

## Current route coverage

This release follows deployment option C. The Studio BFF forwards `/api/v1`
and `/hubs` to the Task Server. The legacy OrchestratorApi bridge serves the
remaining `/api` routes, so some Studio views still use its local data and do
not have distributed route parity. The local Connector remains loopback only;
LAN access to its full route set is not part of this deployment. The
board may show "no runners" even while the Task Server has a registered
Runner; use the Task Server's `/api/v1/runners` route for that inventory. The
[route ownership gap](./docker-compose-connector-gap.md) records the deferred
parity work. AGT-W49 recommends a separate Operations Server backchannel; this
compose keeps the current services until that component has a released image
and contract.

## Operate and verify

```sh
docker compose ps
docker compose logs --tail 100
docker compose down
```

`down` retains the named volumes. See [Docker operations](./docker.md) for
updates, backup, restore, TLS edge, resource limits, and Docker Desktop volume
behavior. Contributors can build the equivalent `-dev` service set with the
`dev` profile; CI runs `scripts/compose-smoke-test.sh` with that profile and a
Playwright browser check of the web and Task Server route.
For a manual source build, set `AGENT_STUDIO_VERSION` to the value in `VERSION`
and name the `-dev` services explicitly so the pulled release services do not
also start.
Release-tag CI runs the same smoke against published images and then runs
`scripts/compose-upgrade-test.sh` from the previous release to the new tag.
The fresh-VM harness is `scripts/compose-smoke-vm-test.sh`.
