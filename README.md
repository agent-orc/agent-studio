# Agent Studio

[![License](https://img.shields.io/github/license/agent-orc/agent-studio)](LICENSE)

No GitHub Release is published yet. To install now, use the source checkout and
the one-box Compose commands in [Install](#install).

**Management layers on top of coding work.** Agents (Claude Code, Codex, GitHub Copilot, Gemini) write the code; this repository is the Studio: a task board, agent pipelines, and project wikis that assign, gate, review, and account for it.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/media/architecture-dark.svg">
  <img alt="Architecture: a browser Studio and a central Task Server on one HTTPS origin (authority channel); Runner-Hosts execute on any host over outbound-only claim/lease/results channels; code travels separately over git origin." src="docs/media/architecture-light.svg" width="760">
</picture>

## What you see

![The board: task cards moving through backlog, active, and review lanes](docs/media/board.png)

![Task detail: pipeline steps, live agent activity, and run evidence](docs/media/task-detail.png)

## What it provides

- Batch remote and local coding-agent work with task management in one place.
- Reduces cognitive load: task state, progress and evidence are visible instead of remembered.
- A management layer over autonomous agent runs: assign, gate, review, account.
- Works with your existing CLI subscriptions, or bring your own API keys.
- Runs fully local or distributed, your choice per project. Project chat follows
  the project's execution location and shows its host, repository checkout,
  branch, and revision.
- Keeps the Task Server as the durable control plane while separately fenced
  Remote Review Executors inspect immutable result revisions.
- Runs review decisions, council reactions, post-processing, gate dispatch, and
  completion judging in the separate API-only Orchestrator Engine. Flow
  definitions and in-flight runs remain durable Task Server data, so restarting
  the Engine does not orphan work.
- Routes operator host inspection, allowlisted restarts, drains, log collection,
  and reviewed configuration apply through the Operations Server and outbound
  host agents in the networked deployment. Short OpenSSH certificates cover a
  fixed command allowlist during migration away from standing operator keys.

## Install

No GitHub Release has been published yet, so the release installer
(`releases/latest/download/install.sh`) returns 404 for now. Until the first
release, the recommended path for one machine is the source-built one-box
stack. It needs Docker Engine with Compose v2 (or Docker Desktop) that your
user can reach without `sudo`, Git, and about 8 GB of disk:

```bash
git clone https://github.com/agent-orc/agent-studio.git && cd agent-studio
scripts/compose-distributed-bootstrap.sh
AGENT_STUDIO_VERSION="$(cat VERSION)" docker compose --profile dev up --build --wait task-server-dev orchestrator-engine-dev studio-bff-dev orchestrator-api-dev web-dev agent-host-distributed-dev agent-host-review-distributed-dev
```

Open `http://127.0.0.1:4011`, sign in with the owner code from
`docker compose exec task-server-dev cat /run/agent-studio-secrets/owner_bootstrap_code`,
and follow [your first task](./docs/operations/setup/your-first-task.md).
The [setup guide](./docs/operations/setup/README.md#choose-your-path) names
the recommended path for every other situation (no Docker, extra execution
hosts, contributing).

The one-box stack starts the Task Server, Engine, Studio BFF, compatibility
API, web UI, and coding and review hosts. It creates restricted principal
credentials in a named volume on first start and reuses them on later starts.
Coding tasks additionally need provider and Git credentials mounted into the
agent host. The default UI listens only on loopback; LAN exposure uses the
optional HTTPS edge and an exact origin allowlist.

After a release is published, its Linux installer checks the distribution and
architecture, verifies the setup executable
against `SHA256SUMS`, then offers the same one-box stack from pinned images,
a native systemd install (root), or an agent host that joins an existing
Studio. Windows users download `agent-studio-setup.exe` and verify it against
`SHA256SUMS`. See the [install guide](./docs/operations/setup/install.md) for
Windows commands, the native and connector profiles, unattended answers,
update, rollback, uninstall, and remote topologies.
See the [Docker operations guide](./docs/operations/setup/docker.md) for
updates, backups, product-managed credential rotation, credential mounts, and
the current route coverage limit beyond `/api/v1`.

## Testing

`scripts/scenario.sh --target inproc --level smoke` runs the deployment
regression scenario: one seeded fixture driven end to end through bootstrap,
claim, run, and auto-review against the Task Server / Runner topology, no
Docker required. See
[docs/operations/testing/deployment-scenario.md](docs/operations/testing/deployment-scenario.md).

## More

Agent Studio is part of the agent-orc ecosystem. It uses
[Chat](https://github.com/agent-orc/chat) for coding-agent conversations and
sits alongside [Runner](https://github.com/agent-orc/runner) for hardened CLI
execution, [Token Economy](https://github.com/agent-orc/token-economy) for
model pricing and usage accounting, and
[Quality Studio](https://github.com/agent-orc/quality-studio) for layered code
review.

For future product direction, see the [roadmap](ROADMAP.md). For architecture,
ADRs, contracts, and the full documentation index, see
[agent-orchestrator.dev](https://agent-orchestrator.dev).

Licensed under the [Apache License 2.0](LICENSE).
