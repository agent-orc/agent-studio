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

## Get started

```bash
git clone https://github.com/agent-orc/agent-studio.git
cd agent-studio
cp .env.example .env
# Replace TASK_SERVER_AUTH_TOKEN in .env before using a distributed profile.
mkdir -p .data/{workspace,projects,task-server,agent-host}
docker compose up --wait
```

Open [http://localhost:4011](http://localhost:4011). Docker Compose is the
primary new-user installation path. It requires at least 8 GB of free disk
space; no host .NET or Node.js install, local settings file, maintainer switch,
or neighbouring repository is required. See the
[setup guide](./docs/operations/setup/getting-started.md) for prerequisites,
persistence, and troubleshooting. As an alternative for Linux x64 release
installs with no source checkout and no .NET prerequisite, the guided
[`agent-orchestrator-setup`](https://github.com/agent-orc/agent-studio/releases/latest/download/agent-orchestrator-setup)
executable offers an isolated Docker demo, a native single-machine install, and
a guided [multi-machine](./docs/operations/setup/multi-machine.md) join flow.
To add execution capacity after the Studio is running, follow the
[Agent Host guide](./docs/operations/setup/linux-runner-host.md). Source
contributors use the explicit `dev` build profile described in the
[container image guide](./docs/operations/setup/task-server.md#container-images)
and the separate
[contributor setup](./docs/operations/setup/contributor-setup.md).

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
