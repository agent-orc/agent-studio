# Layer 3 supervisor scripts

External, read-mostly scripts that run **outside** the running stable instance. The product's runtime never starts these. They live in the dev checkout because the source travels with the rest of the repo, but every script in this folder is invoked by the user (or a host scheduler) from outside the app.

The doctrine for the layer: stable is the single state-machine authority over its own job state (ADR-0017). Anything in this folder either reads stable's state or operates on stable from outside the running process — never both at once, never in a way that lets stable mutate itself while the same `dotnet` / `ng serve` is alive.

## Scripts

### `run-system-review.sh` + `system-review.md` + `system-health-check.mjs`

A read-only review skill for the running stable instance. Drives the `system-review` skill via a CLI (`claude` by default) and writes one Markdown review file under `<workspace>/logs/system-review/<YYYY-MM-DD-HHmm>.md`. See `system-review.md` for the skill itself.

The skill reads **Agent Message Bus** evidence first (`<workspace>/logs/bus/<scope>/<date>.jsonl`, schema in [`docs/system/schemas/agent-message.schema.json`](../../docs/system/schemas/agent-message.schema.json)) and falls back to the legacy raw streams (`logs/meta/<project>/observations.jsonl`, `interventions.jsonl`, per-job `cli-output.log`) when the bus is empty or absent. The eight structured health checks (long silent periods, repeated interventions, repeated failed/cancelled runs, token spikes, supporting jobs without accepted review, stuck loops, weak review evidence, backend crash markers) are implemented in `system-health-check.mjs` so they can run without the CLI session.

Three invocation modes:

```sh
# Full CLI-driven review (default).
./scripts/supervisor/run-system-review.sh

# Dry-run, structured checks only - reads the live workspace bus directory.
# Writes a Markdown report and exits without invoking claude / codex / copilot.
./scripts/supervisor/run-system-review.sh --dry-run

# Dry-run against a hand-built or post-incident JSONL export.
./scripts/supervisor/run-system-review.sh --dry-run --fixture path/to/bus.jsonl
```

A bundled sample fixture lives at `scripts/supervisor/fixtures/sample-bus.jsonl` and exercises every health check; `scripts/supervisor/test-system-health-check.sh` runs the dry-run against it and asserts each check fires. Run cadence: every 4-8 hours. Scheduling is the user's concern; this script is the entry point a cron / Task Scheduler entry can call.

### `dev-lifecycle.sh`

Narrow start/stop/status for the **dev** backend on `:5030`, used as a Playwright target by E2E specs running from stable. This is the only place outside `start-dev.sh` / `stop-dev.sh` that should touch the dev backend.

### `frontend-watchdog.sh` (AGT-2829)

Supervises the frontend dev server (`ng serve`) so a crash does not leave the
seat answering 000 until a human notices. `start-stable.sh` / `start.sh` used
to background `ng serve` once and never look at it again; this script is what
they should delegate to instead:

```sh
DETACH=1 ./scripts/supervisor/frontend-watchdog.sh start   # supervise, return immediately
./scripts/supervisor/frontend-watchdog.sh stop              # stop supervisor + frontend
./scripts/supervisor/frontend-watchdog.sh status             # exit 0 iff healthy
```

It launches `ng serve` directly (`node .../ng.js serve ...`), not `npm start`,
because `npm start`'s `prestart` hook re-lints the whole frontend - and can
hard-fail the boot on an unrelated violation - on every single restart. On an
unexpected exit it restarts with exponential backoff (`FRONTEND_BACKOFF_*_SECONDS`),
logs the exit code and the tail of the frontend's own log to
`.frontend-watchdog/watchdog.log`, and gives up with a clear message after
`FRONTEND_MAX_FAST_CRASHES` (default 5) consecutive crashes inside
`FRONTEND_MIN_UPTIME_SECONDS` (default 10s) of starting, instead of looping
forever. Port defaults follow `api.sh`'s checkout-name convention (`4011` for
a `*-stable` checkout, `4010` otherwise).

`./scripts/supervisor/test-frontend-watchdog.sh` force-kills the running
frontend process and asserts a new one comes up and answers again, then
separately proves the give-up path with a fixture that crashes immediately
every time.

### `restart-stable-after-batch.sh` + `run-stable-restart-watcher.sh`

External orchestrator that restarts stable cleanly at quiet boundaries. The motivation is the same crash class that produced ADR-0020: stable serves the source the running tasks edit, so if stable were ever to "restart itself" mid-batch it would be replacing its own running code. ADR-0021 makes that a hard non-goal and assigns the restart responsibility to this watcher.

The one-shot script has two trigger modes. `review-batch` is the existing
default described below. `ATP_RESTART_TRIGGER=main-advance` instead compares
the Stable checkout with remote `main`; it is the cron-safe deployment handoff
after a successful develop-to-main promotion. Both modes use the same
runner-idle, merge-gate drain, update, logging, and verified-resume path. See the
[promotion runbook](../../docs/operations/develop-main-promotion.md) for the
complete command and cron entry.

Trigger conditions (both must hold on a tick):

- At least N (default 3) **new** job folders have appeared in the watched project's `4-review` lane since the last restart (or since the watcher first booted and took its baseline snapshot).
- Stable's `/api/runner/status` reports every project as idle (`activeJobId == null`). If stable is unreachable, the tick is skipped — never restart blind.

On trigger the watcher delegates to the versioned
[`scripts/update-stable.sh`](../update-stable.sh). The updater runs the preflight
(clean working tree, fast-forward only), stops Stable, updates from
`origin/main`, and runs `npm install` when the frontend package inputs or a
`coding-agent-chat` postinstall patch changed. After an install it removes
`frontend/.angular/cache`, because the postinstall bridge changes dependency
bytes without changing Vite's optimizer cache key. It then launches Stable
and uses `playwright-core` to load the frontend. Any browser `pageerror` makes
the update fail; an open port alone is not health evidence. The updater also
requires host-owned `deploy-task-server.sh` and `start-task-server.sh` wrappers.
After Stable is stopped, it installs the matching Task Server package, starts
the non-interactive Scheduled Task before the API, waits for `/readyz`, and
verifies the management plane both directly and through OrchestratorApi. It
refuses to proceed unless Stable's gitignored
`backend/appsettings.Local.json` selects the same loopback origin. This also
applies to the main-advance watchdog path, so the watcher cannot roll out a
claim-path decoupling release into the old monolith topology. A checkout held
at a detached release stays detached until the Task Server, proxy, and browser
probes all pass; a failed candidate is not attached to `main`.
The watcher does not call `git pull` or `git fetch` directly.

Older devspaces may still have an unversioned root-level `update-stable.sh`.
Replace that copy after updating the dev checkout, or point every caller at the
versioned script:

```sh
install -m 0755 agent-taskboard-dev/scripts/update-stable.sh ./update-stable.sh
```

On the Windows host, the deploy wrapper invokes
`deploy/windows/task-server/install-task-server-release.ps1` with the checkout
and target SHA passed by `update-stable.sh`. The start wrapper calls
`Start-ScheduledTask -TaskName AgentOrchestrator-TaskServer`. Both wrappers run
outside interactive sessions and propagate failures.

The versioned updater can also run in place and remains the source of truth.
`scripts/test-update-stable.sh` covers both the stale prebundle regression and
the hard failure on an injected application boot error.

Immediately before that hard restart, the watcher polls `/healthz/drain`.
`gate-busy` means an accepted delivery is inside the serialized merge, build
gate, and possible rollback boundary. The watcher waits for `idle` for up to
`ATP_GATE_DRAIN_TIMEOUT_SECONDS` (default 120), polling every
`ATP_GATE_DRAIN_POLL_SECONDS` (default 2). When the bounded window expires it
continues with the restart and leaves recovery to the accepted-integration
backstop. An older stable build without the route keeps the previous
runner-idle behavior for rolling-upgrade compatibility.

Each invocation that actually restarts appends one JSON line to `<workspace>/logs/stable-restarts.jsonl`:

```json
{"ts":"2026-05-05T08:42:11Z","event":"restart","trigger":"review-batch","status":"ok","jobsSinceLastRestart":3,"targetMain":"","headBefore":"2bec67c","headAfter":"a1f4b29","durationSeconds":47,"reviewCountAfter":14}
```

The watcher also persists its rolling snapshot of seen `4-review` folders at `<workspace>/logs/stable-restart-watcher/snapshot.txt`. That file plus the JSONL log are the watcher's only state — delete them to reset.

#### Running it

```sh
# foreground (Ctrl-C to stop)
./scripts/supervisor/run-stable-restart-watcher.sh

# tighter tick for an active session
ATP_RESTART_TICK_SECONDS=30 ./scripts/supervisor/run-stable-restart-watcher.sh

# different threshold or workspace
ATP_RESTART_THRESHOLD=5 ATP_WORKSPACE=/some/other/workspace \
  ./scripts/supervisor/run-stable-restart-watcher.sh
```

#### Resume verification after the restart

`update-stable.sh` stops stable, pulls, and restarts. The fresh backend comes back up in whatever runner mode it had before, so for the supervisor's pause-then-update-then-resume recipe the watcher needs an explicit verified resume after the restart finishes. A single missed `PUT /api/runner/<project>/mode` (transient backend-restart race or a missing `X-Client-Id`) silently leaves the project paused — that is the regression that motivated `resume-runner.sh`.

`restart-stable-after-batch.sh` calls `resume-runner.sh` automatically after a successful update. The helper:

1. Polls `/healthz` until 200 (60 s ceiling; treats 503 as "still booting", not a failure).
2. Auto-registers a `service` identity at `POST /api/clients/register` if `ATP_CLIENT_ID` was not provided. Re-registration is idempotent on `displayName`.
3. Sends `PUT /api/runner/<project>/mode` with `X-Client-Id` and `mode=auto-continuous`.
4. Reads `/api/runner/status` back and only declares success when the project's `mode` is `auto-continuous`. On mismatch it retries the PUT with exponential backoff up to `ATP_RESUME_MAX_ATTEMPTS` (default 5).

If the resume cannot be verified the helper exits non-zero so the watcher can log a `resume-failed-rc-N` status in `stable-restarts.jsonl` instead of pretending success.

`./scripts/supervisor/test-resume-runner.sh` exercises the helper against a tiny Python stub backend that returns 503 for the first three healthz polls and only flips its mode on the third PUT. Skips with code 0 when Python 3 is not on PATH.

`./scripts/supervisor/test-restart-stable-gate-drain.sh` proves that a busy
merge gate delays the update call until it becomes idle, and that a gate which
stays busy is released to the hard restart only after the configured bounded
window.

`./scripts/supervisor/test-restart-stable-main-advance.sh` proves that an
unchanged remote `main` is a no-op, a new main SHA is deployed and logged, and
an active Stable run defers deployment.

#### How it relates to the system-review monitor

Both watchers read stable from outside the running process; neither mutates job state. They run independently and can be started in different terminals or under different schedulers. The system review answers "is stable behaving correctly?" — the restart watcher answers "is now a safe moment to swap stable to newer source?". They share a logs root (`<workspace>/logs/`) but write to different files, so a user reviewing recent activity can tail both without crossing wires.

## Env conventions

All scripts in this folder accept the same workspace / stable-checkout overrides:

- `ATP_WORKSPACE` — workspace root (default: `C:/Projects/agent-taskboard-workspace`)
- `ATP_STABLE_CHECKOUT` — stable repo (default: sibling `agent-taskboard-stable`)

Per-script knobs are documented at the top of each script.
