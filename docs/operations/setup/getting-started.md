# Getting started

Docker Compose is the primary path for a new Agent Studio installation.
It gives the application a consistent Linux runtime on Windows, macOS, and
Linux, and keeps .NET, Node.js, local configuration files, maintainer switches,
and repository-neighbour assumptions out of the first run.

As an alternative for release installs on Linux x64, the guided self-contained
`agent-orchestrator-setup` executable needs no source checkout and no .NET
installation:

```sh
curl -fLO https://github.com/agent-orc/agent-studio/releases/latest/download/agent-orchestrator-setup
chmod +x agent-orchestrator-setup
sudo ./agent-orchestrator-setup
```

It offers Demo, Single Machine, Multi-machine Control Plane, and Agent Host
join paths; see [multi-machine.md](./multi-machine.md) for the distributed
flow. The rest of this page describes the Docker Compose path.

The default stack starts the Studio UI and its API. It deliberately does not
start an Agent Host or install coding-agent CLIs. Those execution credentials
belong to the later host-onboarding step, not to the first successful boot.

## 1. Prerequisites

- Git.
- Docker Desktop, or Docker Engine with Docker Compose v2.
- At least 8 GB of free disk space for base images and build layers, plus the
  space needed for persistent task data and managed project checkouts.

You do not need a .NET SDK, Node.js, Git Bash, a second repository,
`appsettings.Local.json`, or a special environment variable.

Confirm Docker before cloning:

```sh
docker version
docker compose version
```

Both commands must show a working client and server. On Linux, your user must
be allowed to access the Docker daemon.

## 2. Install and start

Clone one repository and run one start command:

```sh
git clone https://github.com/agent-orc/agent-studio.git
cd agent-studio
cp .env.example .env
mkdir -p .data/{workspace,projects,task-server,agent-host}
docker compose up --wait
```

Set `AGENT_STUDIO_VERSION` in `.env` to the release tag you intend to deploy.
The first start pulls the release artifacts needed by the selected profiles.
`--wait` returns only after Compose reports the API and browser endpoint
healthy.

The release images run as UID/GID 10001 rather than root. On a native Linux
Docker host, make new bind-mount directories writable by that account before
the first start:

```sh
sudo chown -R 10001:10001 .data
```

Open [http://localhost:4011](http://localhost:4011). A successful first run
shows the empty Agent Studio board. The same end-to-end check is available at:

```sh
curl --fail http://localhost:4011/healthz
```

It returns `"ok"`.

## 3. What the command creates

The default Compose project contains exactly two services:

| Service | Purpose | Host endpoint |
|---|---|---|
| `orchestrator-api` | Board API and current local orchestration runtime | `127.0.0.1:5031` |
| `frontend` | Production Studio bundle and reverse proxy | `0.0.0.0:4011` |

Task data and managed project data live at the host paths configured by
`STUDIO_WORKSPACE_PATH` and `STUDIO_PROJECTS_PATH` in `.env`. Replacing a
container does not delete those directories.

The default ports can be changed when they conflict with another local service:

```sh
STUDIO_UI_PORT=14011 STUDIO_API_PORT=15031 docker compose up --wait
```

This is the same Compose installation path with port overrides, not a second
setup method.

## 4. Stop, restart, and inspect

```sh
docker compose stop
docker compose start --wait
docker compose ps
docker compose logs -f
```

To remove the containers while retaining product data:

```sh
docker compose down
```

Do not add `--volumes` unless you intentionally want to delete the installation
data.

## 5. Add execution capacity

The green board is the first-install boundary. Running coding tasks also
requires an Agent Host with a coding-agent CLI login and repository access.
Follow [Linux runner host](./linux-runner-host.md) for that separate,
credential-bearing host setup. The control plane remains usable while no Agent
Host is connected.

## Maintainer verification

CI runs `scripts/compose-smoke-test.sh`, which proves the documented default
stack, the distributed Task Server and proxy topology, and a containerized
Agent Host claim through Auto Review. Release CI runs it against the published
images; branch CI uses the explicit source-build override.

For a clean-machine proof, `scripts/compose-smoke-vm-test.sh` boots a pinned
Ubuntu 24.04 cloud image with KVM acceleration, installs only Docker and Compose
inside the guest, and runs the same smoke test against a source archive of the
current worktree. The guest has a 24 GB sparse virtual disk. The host needs KVM,
QEMU, cloud-image-utils, genisoimage, 8 GB RAM available to the guest, and about
12 GB of free disk space for the cached base image plus transient build layers.
The harness has no software-emulation fallback, so a nested-virtualization
failure is reported instead of silently running a different test.

On Ubuntu 24.04:

```sh
sudo apt-get install cloud-image-utils genisoimage qemu-system-x86 qemu-utils
scripts/compose-smoke-vm-test.sh
```

## Troubleshooting

| Symptom | Check or fix |
|---|---|
| `docker compose` is not a command | Install Docker Compose v2. Docker's legacy `docker-compose` command is not supported. |
| A port is already allocated | Use the `STUDIO_UI_PORT` and `STUDIO_API_PORT` overrides shown above. |
| `--wait` ends with an unhealthy service | Run `docker compose ps` and `docker compose logs`; the service health checks preserve the failing component. |
| The browser cannot reach `4011` on a remote host | Allow the selected UI port in the host firewall or bind it through your existing private tunnel. The API stays loopback-only by default. |
| Image layers consume too much disk | Inspect with `docker system df`. Do not remove the configured host data paths. |

If you are changing Agent Studio source code rather than installing the product,
use
`docker compose -f docker-compose.yml -f docker-compose.dev.yml --profile dev up --build --wait`
and the separate [contributor setup](./contributor-setup.md).
