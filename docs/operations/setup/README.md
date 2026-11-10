# Setup guide

agent-orchestrator is a local Kanban board that drives your Claude Code, Codex, Copilot, or Gemini CLIs through a sequential task queue per watched project. The product pitch lives in [../../README.md](../../../README.md); the near-term direction lives in [../../ROADMAP.md](../../../ROADMAP.md). The hard rules every CLI driving the repo must follow are in [../../AGENTS.md](../../../AGENTS.md).

This folder is the **operator-facing setup guide**.

## Choose your path

Agent Studio has not published a GitHub Release yet. The release downloads in
[install.md](./install.md) (`releases/latest/download/...`) return 404 until
the first release exists, and the `ghcr.io/agent-orc` images are not publicly
pullable. Until then, every recommended path below starts from a source
checkout of `develop`. Each situation has exactly one recommended path.

| Situation | Recommended path | Prerequisites | Status on `develop` |
|---|---|---|---|
| Run Studio on one machine: evaluate it, or drive your own projects | [Source-built Docker one-box](./docker.md#install-from-this-checkout) | Docker Engine with Compose v2 on Linux, or Docker Desktop on Windows (run the scripts in Git Bash or WSL) or macOS. Your user must reach the Docker daemon without `sudo` (`docker info` works, for example through the `docker` group). Git, at least 8 GB of free disk. No root. | Works. The Compose smoke test builds and starts this stack in CI. |
| Linux host without Docker (native systemd services) | No verified path yet. Until it passes, run the Docker one-box on a machine that has Docker. | Native setup needs root (`sudo`), systemd and x64 Linux. Building its assets from the checkout needs .NET SDK 10 and Node.js 22. | Not verified. `installation-nightly.yml` builds the assets but fails its native install step on Debian, Ubuntu and Fedora in every run since 2026-10-16. |
| Add a Linux execution host to a running Studio | **Workspace Settings > Execution Hosts > Set up agent host**, see [linux-runner-host.md](./linux-runner-host.md#product-onboarding-from-execution-hosts) | SSH key access to the host, passwordless `sudo` (root) there, .NET 10, and a Task Server URL the host can reach | Follows the runbook. Not re-verified for this guide. |
| Edit, test, or debug Agent Studio itself | [Contributor setup](./contributor-setup.md) (`api.sh` and `npm start`) | .NET SDK 10, Node.js 22, Git, bash (Linux, or Git Bash on Windows). No Docker, no root. | Works on Linux runner hosts and Windows. It is a development seat, not a product installation. |

After the one-box stack is up, continue with
[your-first-task.md](./your-first-task.md): owner sign-in, the first-run
checklist, project creation, execution location, pickup mode,
`.agent-studio/project.yml`, review, and Accept.

When the first release is published, [install.md](./install.md) becomes the
recommended path for the first two rows. Its executable then installs the same
one-box stack from pinned images, or the native systemd profile with root.
The release and the source path describe the same one-box stack: Task Server,
Engine, Studio BFF and API, web UI on `http://127.0.0.1:4011`, and one coding
and one review runner.

## Pages

| File | Use it when |
|---|---|
| [install.md](./install.md) | Install a published release (none exists yet): setup executable, checksums, update, rollback, uninstall, native and remote topologies. |
| [getting-started.md](./getting-started.md) | Short pointer to the recommended path above. |
| [docker.md](./docker.md) | The recommended one-box installation from this checkout, plus its operations: credentials, backup, update, and network binding. |
| [contributor-setup.md](./contributor-setup.md) | Development seat for contributors who edit, test, or debug Agent Studio itself on Linux or Windows. Not a product installation path. |
| [onboard-a-project.md](./onboard-a-project.md) | Product workflow for project creation through the UI or API, central task-store rules, runtime activation, and troubleshooting. |
| [onboard-an-agent-cli.md](./onboard-an-agent-cli.md) | A new CLI (Claude, Codex, Copilot, Gemini) needs to be installed and made auto-runnable on this machine. Includes the load-bearing **Codex on Windows sandbox quirk**. |
| [your-first-task.md](./your-first-task.md) | First use after installation: first-run checklist, project creation, execution location, pickup mode, `project.yml`, review, and Accept. |
| [troubleshooting.md](./troubleshooting.md) | FAQ-style: sandbox errors, pickup mode changed after failures, a pickup mode change refused, crash recovery, quota probe stuck on onboarding. |
| [linux-runner-host.md](./linux-runner-host.md) | Running a task on a remote Linux host with the standalone `agent-runner` (RM-5): provision, configure, and drive one task end-to-end via the Task Server API. |
| [workstation-runner-host.md](./workstation-runner-host.md) | Publish the same runner host for a Windows workstation, configure bounded local roots, tools and preview evidence, and verify the claim contract. |
| [preparation-isolation-orchestrator.md](./preparation-isolation-orchestrator.md) | Repository-owned project preparation, content-addressed executor caches, stable checkouts, leased worktrees, and the M1 boundary with later isolation and healing stages. |
| [multi-machine.md](./multi-machine.md) | Guided Linux setup across a Control Plane machine and one or more Agent Hosts, including the join-token flow, topology diagram, and verification. |
| [website-onboarding-template.md](./website-onboarding-template.md) | Source copy for the marketing website download page and remote topology. Website integration remains owned by MKT/AOW. |
| [remote-runner-persistent-connection.md](./remote-runner-persistent-connection.md) | Unattended remote operation: configure the Task Server-owned SSH reverse link, inspect its resource and operator-feed transitions, and use the runner's `--health-check`. The scheduled keeper is an emergency path. |
| [task-server.md](./task-server.md) | Install, configure, supervise, migrate, manage retention policies, run or schedule archive/restore, and create or restore verified full backup sets for the independently deployed Task Server control plane. |
| [installation-recovery.md](./installation-recovery.md) | Capture, copy off-host, verify, and restore the installation recovery set onto an empty target; fence stale hosts, pass the resume gate, and measure recovery point and time with the drill. |
| [update-service.md](./update-service.md) | Operate the loopback Update Service of a dev or Stable checkout: trigger token file, rotation, allowed Studio origins, forced triggers, and settings. |
| [studio-op.md](./studio-op.md) | Install the `studio-op` operator CLI, put the operator token into its owner-only file, and look up its commands, routes and stable exit codes (OP-8). |
| [operator-agent.md](./operator-agent.md) | Give the operator agent its own Studio identity and Owner-started sessions of at most 8 hours, run it under its own OS user without your SSH agent or keys, and configure the off-host audit sink (security model SM-1). |
| [identity-and-project-bootstrap.md](./identity-and-project-bootstrap.md) | First owner with the installer code, recovery custody, operator accounts, one-time host enrolment, canonical project registration and the host repository probe for the standalone Task Server (I05). |
| [remote-compose-test-harness.md](./remote-compose-test-harness.md) | Run the isolated Task Server, Agent Runner, and Studio Compose acceptance harness on a remote Docker host, with deterministic partitions, rolling replacements, evidence export, and identity-scoped cleanup. |
| [networked-deploy.md](./networked-deploy.md) | Configure and run the server-first networked deploy, dry run, timing journal, and rollback from an operator-owned host manifest. |
| [presentation-capture.md](./presentation-capture.md) | Regenerating deterministic presentation stills and recording safe silent loops or narrated backup footage against the ADR-0056 demo workspace. |
| [hosted-wiki-publication.md](./hosted-wiki-publication.md) | Publishing the accepted documentation revision to the hosted Wiki: published-revision rules, freshness SLO, read-only fetch credentials, scheduled and forced triggers, atomic promotion, typed deployment failures, and the rehearsed rollback drill. |
| [docker-compose-connector-gap.md](./docker-compose-connector-gap.md) | Option C decision and the remaining Connector route-coverage limit for the one-box deployment. |
| [move-studio-to-server.md](./move-studio-to-server.md) | Move Studio from Stable on the workstation to the networked monolith stack on the server with one script: inventory, rehearsal, apply, and rollback. |
| [studio-backup-restore.md](./studio-backup-restore.md) | Scheduled encrypted backups of the networked website stack, their off-host copy and Operations page state, escrow of the backup and secret store keys, restore onto a new host, and the optional host-level timer. |

## Related references

- [../skills-architecture.md](../../concepts/skills-architecture.md) - portable-skills doctrine (the `.agents/skills/` library).
- [../../.agents/skills/task-api/SKILL.md](../../../.agents/skills/task-api/SKILL.md) - programmatic task creation / move via the HTTP API.
- [../cli-skills/README.md](../../system/cli/skills/README.md) - per-CLI deep references (frame model, session model, known incidents).
- [../agent-task-contract.md](../../system/contracts/agent-task.md) - the app-owned task lifecycle every watched project inherits.
