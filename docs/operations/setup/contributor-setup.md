# Contributor setup

This is the source-build workflow for contributors who need to edit, test, or
debug Agent Studio itself. It is not a product installation path. Product users
should follow the single Docker Compose path in
[getting-started.md](./getting-started.md).

Everything below was verified against this repository's actual scripts and source on 2026-07-08. All commands are Git Bash (`sh`), not PowerShell.

## 1. Prerequisites

| Requirement | Check | Notes |
|---|---|---|
| Windows | - | The only tested target today. `api.sh` and the other repo scripts are `sh`, run under Git Bash. |
| Git Bash | `bash --version` | Ships with [Git for Windows](https://git-scm.com/download/win). Agents and the repo's own scripts never use PowerShell for these tasks - see [AGENTS.md](../../../AGENTS.md) "Agent shell policy". `api.ps1` exists for manual, human, interactive use only. |
| .NET SDK 10 | `dotnet --version` -> `10.x` | Backend is `backend/OrchestratorApi.csproj`. |
| Node.js 22 | `node --version` -> `v22.x` | Pinned in CI ([.github/workflows/release.yml](../../../.github/workflows/release.yml)). A newer LTS will often work but isn't the tested baseline. |
| Claude Code CLI | `claude --version` | `npm install -g @anthropic-ai/claude-code`. See "Claude Code login" below - this step has a real gotcha. |
| Codex / Copilot / Gemini CLI (optional) | - | Only needed if you plan to run tasks through that CLI. Full install + quirks per CLI: [onboard-an-agent-cli.md](./onboard-an-agent-cli.md) (includes the load-bearing **Codex-on-Windows sandbox** setting). |

### Claude Code login and onboarding (read this before your first run)

```sh
claude
```

Run `claude` once, interactively, in any folder. Log in when prompted, and then **click all the way through the first-run screens** (folder-trust prompt, theme picker, any "try the new renderer" upsell) until you reach the normal `? for shortcuts` prompt. Don't just Ctrl-C out once you're logged in.

Why this matters: the backend's quota probe drives `claude` headlessly and sends `/usage` to read your plan and quota. If the CLI is still sitting on the onboarding wizard the first time the probe runs, `/usage` never reaches the ready prompt and the probe comes back empty. The backend log will say:

```
Claude /usage probe returned 0 windows because the CLI is stuck on a first-run onboarding /
feature-upsell wizard (theme picker or a "Try the new ..." dialog) ...
```

Fix: finish the onboarding wizard once by running `claude` interactively yourself, exactly as above. See [ClaudeQuotaProbe.cs](../../../backend/Features/Cli/Quota/ClaudeQuotaProbe.cs) for the detection logic if you want the details.

## 2. Build and start the development checkout

### 2.1 Clone this repository

```sh
git clone https://github.com/agent-orc/agent-studio.git agent-orchestrator
```

The frontend resolves `coding-agent-chat` from the npm registry. A neighbouring
chat repository is not part of the normal contributor setup.

### 2.2 Backend config

```sh
cd agent-orchestrator/backend
cp appsettings.Local.json.example appsettings.Local.json
```

Edit `appsettings.Local.json` (gitignored, per-checkout) and set at least:

- **`TaskRepository`** - the workspace root where job folders and the project/workspace registry live (`<TaskRepository>/projects/...`, `<TaskRepository>/.metadata/{workspaces,projects}.json`). Point it at an empty folder you own, e.g. `C:\\Projects\\agent-orchestrator-workspace`.
- **`WatchPaths`** - leave it `[]`. It's a bootstrap-only discovery list (ADR-0042); you add your first real project after boot with the in-app dialog, not by hand-editing this file (see step 3).
- **`Environment.IsDev`** - set it to `false` (or delete the whole `Environment` block). This both turns off the orange "DEV" UI markers and matters functionally: the runner's pickup role infers from this flag when `Runner.Role` isn't set explicitly (`true` -> `test-subject`, which *disables* auto-pickup; anything else -> `orchestrator`, which runs it). You want `orchestrator` for a normal instance.
- Delete the `Runner`, `DevTools`, and `//TaskRepository.dev-vs-stable` blocks entirely. Those exist for the maintainers' own **dev + stable** side-by-side setup (see "Reference: dev + stable" below) and aren't needed for a single instance.

### 2.3 Frontend install

```sh
cd ../frontend   # agent-orchestrator/frontend
npm ci
```

Use `npm ci` so the source checkout matches the committed lock file.

### 2.4 Start the backend

```sh
cd ../         # agent-orchestrator/
ATP_ALLOW_DEV_BACKEND=1 ./api.sh start
```

The `ATP_ALLOW_DEV_BACKEND=1` flag is required. `api.sh` refuses to boot a checkout whose folder name doesn't end in `-stable` unless you pass it, or unless a Playwright fixture set `ATP_DEV_BACKEND_FROM_FIXTURE=1` (ADR-0044 - this gate exists to stop the maintainers' own `-dev` checkout from becoming a second, competing auto-pickup driver next to their always-on `-stable` seat). For a single, standalone instance this gate has nothing to protect you from; it's just a one-time acknowledgement.

```sh
./api.sh status     # confirm "running and healthy"
```

Other commands: `./api.sh stop`, `./api.sh restart`. Backend listens on `http://localhost:5030` by default (`PORT` env var overrides it; `api.sh` pins the default to 5031 automatically if your folder name ends in `-stable`, and refuses a mismatched inherited `PORT` unless you set `API_PORT_OVERRIDE=1`).

Each of those commands verifies its own outcome rather than reporting one, because a health check on its own cannot tell a new backend from an old one that never died (AGT-2678):

- `stop` terminates the port listener **and** every `OrchestratorApi` process belonging to that checkout, including an orphan that outlived its launcher, then proves the port is free. If anything survives, it names the PID and exits non-zero instead of printing "API stopped".
- `start` refuses to run when the port is owned by a process it did not launch, and only claims success once the process answering `/healthz` is provably the one it just started. `dotnet run` hands the port to a child, so the launcher PID and the listener PID differ by design; both are tracked.
- `restart` asserts that the PID *and* the process start time changed. A restart that leaves the previous process serving fails.

The sweep is scoped to one checkout and one port: a sibling checkout's backend, and a `dotnet test` run from your own checkout, are never touched. `bash tools/api-restart-selfcheck.sh` proves all of this in about a minute without needing a .NET build; add `--live <checkout>` to prove it against a running backend instead.

### 2.5 Start the frontend

```sh
npm start --prefix frontend
```

(or the VS Code task "Frontend: Start"). Serves on `http://localhost:4010`, proxying `/api` and `/hubs` to the backend on `:5030` (`frontend/proxy.conf.json`). Open it in a browser - you should see the board, empty, with no projects yet.

### 2.6 Optional: the self-update service

Only needed if you want the in-app Update Center to work. `./update-service.sh start` runs a small standalone process on port 5039 that survives backend restarts during an update.

## 3. Onboard your first project

**Recommended: the in-app "Onboard Project" dialog**, not hand-editing config files.

1. In the board, use the workspace "+" in the sidebar to create a workspace if you don't have one yet (`+ Workspace`, name it anything).
2. Click "+" next to that workspace to open **Onboard Project**. Fill in a display name (a short code is auto-derived and editable), pick a default CLI + model, and - important - set **CLI working directory** (`rootPath`) to the target project's folder on disk. Without it the project has no auto-pickup runner and the mode toggle won't work later.
3. Submit. This calls `POST /api/projects` and provisions the project's lane folders immediately - **no backend restart needed.**

This is the current, working path (`RegistryEndpoints.cs`, ADR-0042/ADR-0046). Note on scope: the persistent **Orchestrator Chat** side panel (the chat window docked to a project) is great for asking the orchestrator questions about a project once it's onboarded, but it cannot create a project registration for you today - that capability is still on the roadmap (see [orchestrator-chat.md](../../concepts/orchestrator-chat.md)). Use the dialog above for the actual creation step.

Two cases the dialog doesn't cover, still documented in [onboard-a-project.md](./onboard-a-project.md):

- **Legacy `WatchPaths` bootstrap** (kept for reference / scripted setups) - editing `appsettings.Local.json` directly and restarting.
- **Repo root differs from the CLI working directory** (monorepo, app nested under a parent repo) - set `RepositoryPath` via `PUT /api/projects/{id}` after creating the project, so Git status/diff/commits resolve from the right folder.

That's the whole onboarding step: **register the project, and (if you want the project wiki) enable a wiki-writing pipeline step.** There is no separate "bootstrap the wiki" action and no "not-onboarded" state to clear. Each wiki-writing step (`post-wiki-maintenance`, `post-wiki-learnings`, `post-agents-wiki-sync`) *self-provisions* its own home under the project's `docs/` (`common-problems/`, `learnings/`, `concepts/designated-topics/`) the first time it runs, idempotently and without overwriting existing content. Details: [onboard-a-project.md](./onboard-a-project.md).

### Onboarding checklist: registry + working directory + build profile belong together

Three things describe one project and should be decided in the same sitting. Skipping the third is the trap that broke a real onboarding (a fresh repo escalated its review because the gate ran a Studio-specific build command that did not exist there - AGT-1919 / TE-2):

1. **Registry entry** - the project registration itself (`POST /api/projects`). Without it there is no board column and no runner.
2. **Working directory** (`rootPath`, and `RepositoryPath` when the repo root differs from the CLI working dir) - where the CLI runs and where Git status/diff resolve. Without it the project has no auto-pickup runner and the mode toggle won't work.
3. **Build profile / verify gate** - *how this project is verified*. You usually get this for free: the deterministic build/test gate (`post-build-test-gate`) **derives** its verify commands from the repo layout, so there is nothing to configure for a conventional stack:
   - a `.sln`, `.slnx`, or `.csproj` at the **repo root** -> bare `dotnet build` + `dotnet test` (auto-discovery, no hardcoded project path);
   - a `package.json` (repo root or one level down, e.g. `frontend/`) -> `npm run build` / `npm test` / `npm run lint`, but only for the scripts that manifest actually declares;
   - a repo with both -> both.

   Gates run in disposable workspaces and prepare dependencies before the
   derived commands: root .NET entry points receive `dotnet restore`, and each
   selected Node package directory receives `npm ci` with a shared cache outside
   the workspace. An explicit profile `installCmd` replaces that automatic
   preparation. Profile install, build, and test commands all run through
   `bash -lc`, matching profile validation on Windows and Unix hosts. A failed
   command's durable gate reason includes a bounded stderr/stdout excerpt; the
   complete streams remain in its gate log.

   If nothing is derivable (no root solution/project, no usable npm scripts),
   the gate records `NotApplicable` with reason `no verify commands derivable`;
   the board, task detail, and timeline render the neutral `No build/test
   defined` state. This is distinct from `Skipped`, which means a required gate
   did not reach a verdict and remains an attention state. Project Settings
   keeps a neutral reminder visible while the derived plan is empty. When your
   project needs a non-conventional command (a `make` target, a monorepo build,
   a nested solution), declare an explicit **build profile** with `PUT
   /api/projects/{project}/build-profile` (`buildCmds` / `testCmds`); those
   commands are the override and take precedence over the derivation. See
   [onboard-a-project.md](./onboard-a-project.md) for the build-profile fields.

## 4. Run your first task

Full walkthrough, including what to queue and what to watch for: [your-first-task.md](./your-first-task.md). Short version:

1. Create a small, scoped, read-then-write task (the "Project Overview Doc" pattern in that doc works well) in `2-ready`, targeting the CLI you configured.
2. Set the project's runner mode. Modes are `manual` (you click Start), `auto-single` (picks up one job, then stops), `auto-continuous` (keeps draining `2-ready` on its own), and `paused`. Set it from the project switcher / header pause toggle, or `PUT /api/runner/<project>/mode`.
3. Watch the card move `2-ready -> 3-progress -> 4-auto-review -> 5-human-review`. The Activity Log streams the CLI's tool calls live; the detail panel shows a live `git diff` once the agent starts writing.
4. Logs and evidence live in the job folder: `logs/cli-output.log` (raw CLI stdout/stderr) and `results/` (screenshots and other evidence the run produced).

Scripting task creation instead of clicking through the dialog: the Task API skill ([.agents/skills/task-api/SKILL.md](../../../.agents/skills/task-api/SKILL.md)) is the canonical, agent-neutral reference - read it before writing any script that creates or moves tasks; it covers the `watchPath` vs `rootPath` mixup that trips up everyone once.

## 5. Troubleshooting quick list

| Symptom | Cause | Fix |
|---|---|---|
| `./api.sh start` refuses with "refusing to start the dev backend" | ADR-0044 policy gate (see step 2.4) | `ATP_ALLOW_DEV_BACKEND=1 ./api.sh start` |
| Claude quota panel is empty / plan shows unknown | Claude CLI never finished its first-run onboarding wizard | Run `claude` interactively once and click through to the ready prompt; see step 1. Full detail: [troubleshooting.md](./troubleshooting.md#claude-quota-panel-is-empty-plan-shows-unknown). |
| `npm ci` fails while resolving `coding-agent-chat` | The npm registry is unavailable or the lock file and manifest have drifted | Restore registry access and verify `frontend/package.json` and `frontend/package-lock.json` agree. Do not add a relative `file:` dependency as a workaround. |
| `claude` / `gemini` command is missing or broken on Windows after an interrupted npm update | Half-completed npm install left orphan shim files or a stub binary | `bash tools/check-cli-shims.sh` - self-heals and re-verifies with `claude --version`. |
| Port 5030 / 4010 (or 5031 / 4011) already in use | A previous `dotnet run` or `ng serve` is still listening | `./api.sh stop` (sweeps the port owner and this checkout's backend processes, not just the tracked PID); for the frontend, stop the other `ng serve` or pass `--port <n>`. |
| A newly-added project's mode toggle returns `400 Invalid project or mode` | Per-project runners are only created at backend startup | `./api.sh restart`. Full detail: [troubleshooting.md](./troubleshooting.md#put-apirunnerprojectmode-returns-400). |
| `./api.sh restart` succeeds but the change is not live, or a rebuild cannot write `backend/bin` | Fixed in AGT-2678; an older `api.sh` left the previous process alive and reported success anyway | Update the checkout, then confirm with `bash tools/api-restart-selfcheck.sh`. Full detail: [troubleshooting.md](./troubleshooting.md#apish-restart-said-it-worked-but-the-old-code-is-still-being-served). |

For anything not on this short list, the full FAQ is [troubleshooting.md](./troubleshooting.md), and CLI-specific quirks (Codex's Windows sandbox setting, Copilot's auth, Gemini's stdout buffering) are in [onboard-an-agent-cli.md](./onboard-an-agent-cli.md).

---

## Reference: dev + stable side-by-side (optional, advanced)

Everything above gives you **one** working instance. The maintainers additionally run two checkouts side by side on the same machine - `-dev` for active development (backend `:5030` / frontend `:4010`) and `-stable` for the always-on orchestrator seat that actually manages projects (backend `:5031` / frontend `:4011`) - with small outer wrapper scripts (`start-dev.sh`, `start-stable.sh`, `stop-dev.sh`, `stop-stable.sh`) that live **one level above both checkouts**, not inside this repository. The Stable updater is versioned at [`scripts/update-stable.sh`](../../../scripts/update-stable.sh); invoke it there or copy it unchanged to the outer devspace root for compatibility with an existing caller. That pattern is only worth replicating if you are also developing agent-orchestrator itself against a live reference instance; it is not required to use the product. If you do want it, the shape is: two checkouts named so one ends in `-stable`, a shared workspace-root scripts folder that sets `PORT`/`--port` per checkout before delegating to each checkout's own `api.sh`, and the ADR-0044 gate described in step 2.4 left in place (the `-stable` checkout is exempt from it; the `-dev` one is not).

The outer wrappers must **delegate** their process control to `api.sh` rather than reimplement it. `stop-stable.sh` in particular has to propagate `api.sh stop`'s exit code, because [`scripts/update-stable.sh`](../../../scripts/update-stable.sh) fast-forwards the checkout and reinstalls frontend dependencies immediately after calling it. A stop that returns 0 while the old backend is still running is how a rollout serves stale code and a rebuild fails to copy into `backend/bin` (AGT-2678). An outer wrapper that kills a bare PID, or that ends in `|| true`, throws away exactly the verification this depends on. After changing a wrapper, prove the seat still restarts honestly:

```sh
bash tools/api-restart-selfcheck.sh --live ../agent-taskboard-stable
```

The repository's development start path (`npm start`, which delegates to
`ng serve`) does not install dependencies and does not run the postinstall
patches. It therefore does not invalidate `frontend/.angular/cache`. Keep an
outer `start.sh` or `start-dev.sh` start-only as well. If a local wrapper is
changed to run `npm install`, it must remove `frontend/.angular/cache` after the
install and before starting the frontend, for the same in-place dependency
patch reason as the Stable updater.

`start-stable.sh` (and `start.sh`/`start-dev.sh` if you want the same
protection for `-dev`) must **delegate** the frontend launch to the versioned
[`scripts/supervisor/frontend-watchdog.sh`](../../../scripts/supervisor/frontend-watchdog.sh)
(`DETACH=1 ./scripts/supervisor/frontend-watchdog.sh start`) instead of
backgrounding `ng serve` directly. A wrapper that spawns the dev server once
and exits leaves nobody watching it: when the process dies - OOM, a crashed
dev-server optimizer, an operator's stray `taskkill` - the Stable seat answers
000 until a human notices. The watchdog restarts the frontend with backoff on
an unexpected exit, logs each restart with its exit code and the tail of its
log under `.frontend-watchdog/`, and gives up with a clear message after
repeated fast crashes instead of looping forever. Pair it with
`./scripts/supervisor/frontend-watchdog.sh stop` in `stop-stable.sh`.
`scripts/supervisor/test-frontend-watchdog.sh` force-kills the frontend and
proves it comes back, and proves the give-up path fires on a crash loop.

## Reference: configuration knobs

### Dev vs. stable checkout markers

The frontend visually marks a checkout as "dev" (orange "DEV" stripe, orange PWA icon, `(DEV)` window title) whenever the backend serves `/api/environment` with `{ isDev: true }`, which happens iff `Environment.IsDev` is `true` in `appsettings.Local.json`. Leave it `false` (or omit the block) for a plain single-instance setup.

### Keep the system awake during runs

```json
// backend/appsettings.json (or appsettings.Local.json)
{
  "KeepAwakeDuringRuns": true
}
```

Default `true`. Holds a Windows power request (`PowerRequestSystemRequired`, visible via `powercfg /requests`) for as long as at least one agent run is active, so the host doesn't idle-sleep mid-run; the display can still sleep. The runner is also sleep-aware: on wake it resets each active run's silence clock instead of the watchdog mistaking a nap for agent silence.

### Orchestrator + supervisor toggles

The flags that gate the auto-review orchestrator, the orchestrator-prep lane, the Layer-2 supervisor passes, the Layer-2.5 meta-cycle, and the auto-intervention policy are reachable from the header `⋮` menu -> "Orchestrator config" (`GET`/`PUT /api/admin/config/orchestrator`). Changes land in `appsettings.Local.json` and need a backend restart.

### Job organization through the API

Agents and scripts must organize jobs through the application API, not by directly creating, moving, deleting, or reordering folders under `<TaskRepository>/projects/<projectKey>/`. See the Task API skill for the full endpoint list; the load-bearing ones are `GET /api/watch-paths`, `POST /api/tasks`, `POST /api/tasks/{jobId}/move`, `POST /api/tasks/reorder`, `DELETE /api/tasks/{jobId}`.

### Supported CLIs

Claude Code, Codex, GitHub Copilot, Gemini. The cross-CLI contract (process lifecycle, session model, model selection, quota probing, logging, cancellation) is in [supported-clis.md](../../system/cli/supported-clis.md).

### Keeping target projects in sync

When the agent task contract or folder schema changes, run the `/sync-target-instructions` prompt against each watched project.
