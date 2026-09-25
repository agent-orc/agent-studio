# Website onboarding copy template

> Publishing note: this is source copy for the marketing website. MKT/AOW owns
> website integration, layout, analytics, and final publication. Product
> engineering maintains the commands and product claims below.

## Download Agent Studio for Linux

Download the guided Linux x64 setup executable:

[Download `agent-orchestrator-setup`](https://github.com/agent-orc/agent-studio/releases/latest/download/agent-orchestrator-setup)

Verify it before running:

```sh
curl -fLO https://github.com/agent-orc/agent-studio/releases/latest/download/agent-orchestrator-setup
curl -fLO https://github.com/agent-orc/agent-studio/releases/latest/download/SHA256SUMS
grep '  agent-orchestrator-setup$' SHA256SUMS | sha256sum -c -
chmod +x agent-orchestrator-setup
```

The setup executable and installed product binaries are self-contained. You do
not need to install .NET.

## Choose a path

### Install with Docker Compose

Use the root Compose file for the distributed product. Docker Engine with
Compose v2 or Docker Desktop is required.

```sh
cp .env.example .env
docker compose up -d --wait
```

The UI binds to loopback, and the stack retains task data and generated
principal credentials in Docker volumes. See [Getting started](./getting-started.md).

### Run everything on one machine

Use this path when the Control Plane, repositories, Git credentials, and coding
CLI are on the same Linux machine.

Authenticate Codex or Claude on that machine, then run:

```sh
sudo ./agent-orchestrator-setup --mode single
```

The guide checks prerequisites, asks for the Linux execution user and bootstrap
values, installs the Control Plane and Agent Host, and verifies host
registration. Docker is not required.

### Use several machines

Install the Control Plane first:

```sh
sudo ./agent-orchestrator-setup --mode control-plane
```

Setup prints a join command and protected join token. On every Agent Host
machine:

```sh
sudo ./agent-orchestrator-setup --join
```

Paste the token at the hidden prompt. Each host needs Git and its own
authenticated Codex or Claude CLI. The Control Plane needs neither. See the
[multi-machine guide](./multi-machine.md) for the topology, TLS boundary, GitHub
token permissions, role separation, and verification steps.

## What setup checks

- Linux x64 and required operating-system commands
- Docker Compose only for the demo path
- Git and a host-owned Agent CLI login only for Agent Host paths
- SHA-256 of every downloaded release asset
- Task Server readiness and Agent Host registration

Release lifecycle and rollback details are in the
[release runbook](../releases.md).
