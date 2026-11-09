# Troubleshooting

FAQ-style: known failure modes you may hit while operating agent-orchestrator, what they look like, and what to do. New entries belong here when the symptom is recurring and the root cause is non-obvious from the UI.

For deeper context, the structural references are:

- [../filesystem-contract.md](../../system/contracts/filesystem.md) - lane catalog, on-disk shape.
- [../../.agents/skills/task-api/references/known-pitfalls.md](../../../.agents/skills/task-api/references/known-pitfalls.md) - operator-side pitfalls when scripting via the API.
- [../cli-skills/](../../system/cli/skills) - per-CLI quirks and known incidents.

## Roll back attempt authority from schema 7 to schema 6

Stop the Studio backend so no process can mutate the workspace. Keep a copy of
the entire `.metadata` directory, including the immutable
`attempt-authority.schema6-backup.json` written before the first schema-7
snapshot. Run the **new** backend build against the same TaskRepository root:

```sh
dotnet OrchestratorApi.dll --downgrade-attempt-authority-schema6 <TaskRepository>
```

The command replays pending journal entries, restores inline subjects and
reclaim receipts in the live snapshot and archive shards, copies archived
current attempts back into the live snapshot, writes schema 6, and
moves the schema-7 journals aside with a `.schema7-backup` suffix. It writes
both the schema-6 primary and its `.bak` before moving either journal, so an
interrupted downgrade retains a recoverable snapshot with the journals needed
by an older backup. Verify that
`attempt-authority.json` has `schemaVersion: 6` and inline `subjectId` values
in both `reviewAttempts` and `currentSubjectByTask`. Start the old build only
after the command exits successfully. Keep the subject, receipt, and backup
files until the rollback is no longer needed.

## "The agent only emits sandbox errors"

Symptom: every shell command the agent tries to run fails with `windows sandbox: runner error: CreateProcessAsUserW failed: 1312`. The Activity Log fills with the same error and the run produces no edits.

Cause: Codex's Windows sandbox is set to `elevated`, which refuses every child-process spawn.

Fix: open `~/.codex/config.toml` and set:

```toml
[windows]
sandbox = "workspace-write"
```

Restart the Codex CLI (no backend restart needed - the next pickup re-spawns Codex with the new config). The full background and the runner's preventive complement live in [onboard-an-agent-cli.md](./onboard-an-agent-cli.md) under "Codex on Windows: the sandbox quirk".

## "Pickup mode flipped from Auto to Manual after a short time"

Symptom: the project's **Pickup mode** (Project Settings > Execution location)
changes from **Auto** to **Manual** after a small number of failures, an infra
halt banner names the CLI, and no further **Ready** tasks are picked up.

Cause: the cross-slug infra circuit breaker ([CrossSlugInfraCircuitBreaker.cs](../../../backend/Features/Runner/CrossSlugInfraCircuitBreaker.cs)) saw several different tasks dead-lettered for the same CLI within its window (sandbox errors, CLI crashes, missing-sentinel runs). The runner switches to manual so a misconfigured CLI cannot drain the queue.

What to check:

1. Look at the most recent tasks in **In Progress**, plus any the pickup loop just returned to **Ready** (spawn failure) or escalated to **Human review** (task-shaped failure). Same failure category across them?
2. The banner names the CLI and suggests `tools/check-cli-shims.sh` or `<cli> --version`. The bus event stream (`logs/bus/<project>/<date>.jsonl`) carries the breaker transition and its reason.
3. Fix the underlying cause (sandbox config, CLI install, network), then set **Pickup mode** back to **Auto**.

## "Ready tasks wait: Local coding is disabled on this orchestrator"

Symptom: tasks stay in **Ready** with pickup mode **Auto**, and the
**Execution location** card in Project Settings says "Local coding is disabled
on this orchestrator. Ready coding tasks will wait until this project is
routed to a remote runner."

Cause: the project's execution location is **Local host**. The orchestrator
does not run coding tasks on its own host; coding runs on an execution host.

Fix: select an execution host in the card's **Execution location** list (in
the Docker one-box, `distributed-runner`). If none is listed, attach one, see
[linux-runner-host.md](./linux-runner-host.md).

## "Counters look wrong for a project"

Symptom: per-project counters show numbers that don't match the lanes on the
board, or counts from a different project.

Cause: a cross-project leak in the counter aggregation was reported as
`bug-board-shows-wrong-project-counter-cross-project-leak` (May 2026). This
guide could not confirm a fix.

Workaround: refresh the page; the counts recompute from `/api/tasks/grouped`
on load. If the leak persists, capture that response and attach it to the
existing bug instead of opening a new one.

## Fixed issues that older notes still mention

These symptoms came from defects that are fixed on current `develop`. If one
reappears, treat it as a regression and open a new card with the evidence.

| Old symptom | Fixed by | If you still see it |
|---|---|---|
| Two cards in **In Progress** after an auto-review reissue | `fix-auto-review-reissue-must-go-to-ready-not-progress` (2026-05-11): reissues land at the top of **Ready** | Check each card's `cli-output.log` for `Decision: reissue`. |
| Every review aspect reads "Aspect runner produced no parseable verdict" | 2026-05-11: the aspect runner reads its prompt from stdin instead of argv | Treat aspect rows from before that date as no signal. |
| `./api.sh restart` reports success but the old process keeps serving | AGT-2678 (2026-08-24): `stop`, `start` and `restart` verify the process | Run `bash tools/api-restart-selfcheck.sh`; see [hollow-api-restart](../common-problems/hollow-api-restart/). |
| `PUT /api/runner/<project>/mode` returns `400 Invalid project or mode` for a new project | Projects created through the dialog or `POST /api/projects` get their runner at once; the message no longer exists | See the next section for the current responses. |

## A pickup mode change is refused

Symptom: setting the pickup mode (Project Settings > Execution location, or
`PUT /api/runner/<project>/mode`) fails with one of:

- `409` "Project '<name>' has no RootPath configured, so its runner was never
  started." The project has no working directory, so no local runner exists.
  Set **Working directory** (and **Repository checkout path**) in Project
  Settings, or select a remote execution host.
- `404` "Unknown project '<name>'". The name does not match a registered
  project. Use the exact display name, or the project id.
- `400` "Invalid mode '<mode>'. Allowed: manual, auto-single, auto-continuous,
  paused." The raw runner route still uses the old mode names. Project
  Settings writes `auto`, `manual` or `paused` through
  `PUT /api/projects/<project>/execution-runner` instead.

## "Codex run lands as missing-terminal-sentinel"

Symptom: a Codex task finishes the work cleanly but the run is marked `missing-terminal-sentinel` instead of reaching **Post Processing** and **Human review** with a clean Done.

Cause: Codex has no `--append-system-prompt` flag, so the sentinel grammar is only injected via `CodexCliService.BuildSystemPromptPrefix`. On a resume turn the fresh-start template is not re-rendered. Without the prefix, Codex regularly drops the terminal sentinel.

Fix: the runner already prepends the prefix on every invocation. If you see this on runs that came from the runner (not a manual `codex exec`), check that the prefix length-guard test (`CodexCliServiceTests.BuildSystemPromptPrefix_StaysShort`) didn't get accidentally stripped. The grammar lives in [../agent-task-contract.md](../../system/contracts/agent-task.md).

## "Empty shell folder in a lane"

Symptom: a lane folder exists with only a `logs/` subdirectory; no `job.json`, no `prompt.md`. The API refuses to move or delete it because there is no `job.json` to identify the job.

Cause: orchestrator crash mid-transition or a multi-lane race.

Fix: use a dedicated recovery/delete API when one exists. If the current API
cannot see the shell folder, stop and ask for an explicit operator decision
before any filesystem cleanup; do not hide direct deletion inside an automated
triage script.

See [../../.agents/skills/task-api/references/known-pitfalls.md](../../../.agents/skills/task-api/references/known-pitfalls.md) §7.

## "Studio asks me to review recovered working-tree changes"

Symptom: after a backend restart, a crash recovery entry opens the dialog
"Review recovered working-tree changes" (or "Checkout lags behind ...") with
the project, task, and changed files.

Cause: on boot, crash recovery found uncommitted changes in a project's
working tree. It no longer commits them on its own; it queues a pending item
for you ([CrashRecoveryService.cs](../../../backend/Features/Runner/CrashRecoveryService.cs)).

Fix: review the listed files. Confirm the commit in the dialog only when the
changes belong to the task; it commits with the `crash-recovery` author tag
and never pushes. A ref-drift item (the checkout lags behind its branch) offers
no commit. Commit your own edits before restarting the backend to avoid the
prompt.

## "I want to script a job move but the API rejects it"

Symptom: `POST /api/tasks/<id>/move` returns `409 Job already exists or invalid input` even though the slug is unique.

Cause: you passed `rootPath` as `watchPath`. The server resolves jobs against the *resolved job-folder root* under the workspace, not the project's source tree.

Fix: pass the project's short code or `PROJ-NNN` id in the `project` field (or query parameter), or the `path` field returned by `GET /api/watch-paths`, never `rootPath`. The full pitfall and ready-to-use Node templates live in [../../.agents/skills/task-api/SKILL.md](../../../.agents/skills/task-api/SKILL.md).

## "Claude quota panel is empty / plan shows unknown"

Symptom: the Claude quota widget (status bar donut, admin panel) shows no usage windows and no plan, even though `claude` works fine when you run it yourself.

Cause: the backend's quota probe drives `claude` headlessly and sends `/usage`. If the CLI has never been taken past its first-run onboarding (folder-trust dialog, theme picker, a "try the new renderer" upsell), those screens sit in front of the ready REPL and swallow the `/usage` command, so the probe returns zero windows. `ClaudeQuotaProbe.LooksLikeOnboardingWizard` (see [ClaudeQuotaProbe.cs](../../../backend/Features/Cli/Quota/ClaudeQuotaProbe.cs)) detects this case and logs it distinctly from an actual `/usage` output-format drift.

Fix: run `claude` interactively yourself once, log in, and click through every first-run screen until you reach the normal `? for shortcuts` prompt. The onboarding state persists in `~/.claude.json`, so this is a one-time fix per machine/user. See [contributor-setup.md](./contributor-setup.md#claude-code-login-and-onboarding-read-this-before-your-first-run) for the full first-install version of this; on a runner host, sign in through the first-run checklist's **Provider login on an execution host** step.
