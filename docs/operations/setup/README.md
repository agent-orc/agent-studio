# Setup guide

agent-orchestrator is a local Kanban board that drives your Claude Code, Codex, Copilot, or Gemini CLIs through a sequential task queue per watched project. The product pitch lives in [../../README.md](../../../README.md); the near-term direction lives in [../../ROADMAP.md](../../../ROADMAP.md). The hard rules every CLI driving the repo must follow are in [../../AGENTS.md](../../../AGENTS.md).

This folder is the **operator-facing setup guide**.
[getting-started.md](./getting-started.md) is the single new-user installation
path. The other pages cover contributor builds, attaching a project, onboarding
an agent CLI, first tasks, and troubleshooting.

## Pages

| File | Use it when |
|---|---|
| [getting-started.md](./getting-started.md) | The single new-user installation path: Docker Compose prerequisites, one start command, health checks, persistence, and troubleshooting - start here. |
| [contributor-setup.md](./contributor-setup.md) | Source-build workflow for contributors who need to edit, test, or debug Agent Studio itself. Not a product installation path. |
| [onboard-a-project.md](./onboard-a-project.md) | Product workflow for project creation through the UI or API, central task-store rules, runtime activation, and troubleshooting. |
| [onboard-an-agent-cli.md](./onboard-an-agent-cli.md) | A new CLI (Claude, Codex, Copilot, Gemini) needs to be installed and made auto-runnable on this machine. Includes the load-bearing **Codex on Windows sandbox quirk**. |
| [your-first-task.md](./your-first-task.md) | First time using the board on a new project: what to queue, how to watch it run, what counts as a good first task vs. an anti-pattern. |
| [troubleshooting.md](./troubleshooting.md) | FAQ-style: "agent only shows sandbox errors", "auto-mode flipped to manual", "two jobs in 3-progress", "header counters look wrong". |
| [linux-runner-host.md](./linux-runner-host.md) | Running a task on a remote Linux host with the standalone `agent-runner` (RM-5): provision, configure, and drive one task end-to-end via the Task Server API. |
| [preparation-isolation-orchestrator.md](./preparation-isolation-orchestrator.md) | Repository-owned project preparation, content-addressed executor caches, stable checkouts, leased worktrees, and the M1 boundary with later isolation and healing stages. |
| [multi-machine.md](./multi-machine.md) | Guided Linux setup across a Control Plane machine and one or more Agent Hosts, including the join-token flow, topology diagram, and verification. |
| [website-onboarding-template.md](./website-onboarding-template.md) | Source copy for the marketing website download page: Demo, Single Machine, and Multi Machine paths. Website integration remains owned by MKT/AOW. |
| [remote-runner-persistent-connection.md](./remote-runner-persistent-connection.md) | Unattended remote operation: keep the SSH tunnel to the Task Server up as a supervised, auto-reconnecting service (autossh/systemd or a Windows scheduled task) and use the runner's `--health-check`. |
| [task-server.md](./task-server.md) | Install, configure, supervise, migrate, manage retention policies, run or schedule archive/restore, and create or restore verified full backup sets for the independently deployed Task Server control plane. |
| [remote-compose-test-harness.md](./remote-compose-test-harness.md) | Run the isolated Task Server, Agent Runner, and Studio Compose acceptance harness on a remote Docker host, with deterministic partitions, rolling replacements, evidence export, and identity-scoped cleanup. |
| [presentation-capture.md](./presentation-capture.md) | Regenerating deterministic presentation stills and recording safe silent loops or narrated backup footage against the ADR-0056 demo workspace. |

## Related references

- [../skills-architecture.md](../../concepts/skills-architecture.md) - portable-skills doctrine (the `.agents/skills/` library).
- [../../.agents/skills/task-api/SKILL.md](../../../.agents/skills/task-api/SKILL.md) - programmatic task creation / move via the HTTP API.
- [../cli-skills/README.md](../../system/cli/skills/README.md) - per-CLI deep references (frame model, session model, known incidents).
- [../agent-task-contract.md](../../system/contracts/agent-task.md) - the app-owned task lifecycle every watched project inherits.
