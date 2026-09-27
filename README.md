# Agent Studio

[![License](https://img.shields.io/github/license/agent-orc/agent-studio)](LICENSE)

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
  the project's execution runner and shows its host, repository checkout,
  branch, and revision.
- Keeps the Task Server as the durable control plane while separately fenced
  Remote Review Executors inspect immutable result revisions.
- Runs review decisions, council reactions, post-processing, gate dispatch, and
  completion judging in the separate API-only Orchestrator Engine. Flow
  definitions and in-flight runs remain durable Task Server data, so restarting
  the Engine does not orphan work.

## Install with Docker

```bash
git clone https://github.com/agent-orc/agent-studio.git
cd agent-studio
docker compose --profile dev up --build --wait task-server-dev orchestrator-engine-dev studio-bff-dev orchestrator-api-dev web-dev agent-host-distributed-dev
```

Open [http://localhost:4011](http://localhost:4011). This source-built Compose
path is the verified installation path for this checkout. The one-box stack
starts the Task Server, Engine, Studio BFF, compatibility API, web UI, and an agent host.
It creates restricted principal credentials in a named volume on first start
and reuses them on later starts. Coding tasks additionally need provider and
Git credentials mounted into the agent host.

To run a published release after its images pass post-release CI, copy
`.env.example` to `.env`, set `AGENT_STUDIO_VERSION` to that release version,
and run `docker compose up -d --wait`. The default UI listens only on loopback;
LAN exposure is an explicit `.env` setting. See the
[Docker operations guide](./docs/operations/setup/docker.md) for updates,
backups, product-managed credential rotation, credential mounts, and the current
route coverage limit beyond `/api/v1`.

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
