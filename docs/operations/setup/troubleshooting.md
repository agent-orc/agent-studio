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
reclaim receipts in the live snapshot and archive shards, writes schema 6, and
moves the schema-7 journals aside with a `.schema7-backup` suffix. Verify that
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

## "Auto-mode flipped to Manual after a short time"

Symptom: the project's runner-mode pill flips from `auto-continuous` to `manual` after a small number of failures, no further `2-ready` jobs are picked up.

Cause: the cross-slug infra circuit breaker ([../../backend/Services/Runner/CrossSlugInfraCircuitBreaker.cs](../../../backend/Services/Runner/CrossSlugInfraCircuitBreaker.cs)) tripped on consecutive infra-class failures (sandbox errors, CLI crashes, missing-sentinel runs). The runner falls back to manual to keep a misconfigured CLI from burning through the queue.

What to check:

1. Look at the most recent jobs in `3-progress`, plus any the pickup loop just returned to `2-ready` (spawn failure) or escalated to `5-human-review` (task-shaped failure). Same failure category across them?
2. The bus event stream (`logs/bus/<project>/<date>.jsonl`) carries the circuit-breaker transitions and the reason it tripped.
3. Fix the underlying cause (sandbox config, CLI install, network), then resume manually with the runner-mode pill.

## "Counters in the header look wrong"

Symptom: the per-project counters in the header strip show numbers that don't match the lanes you see on the board, or show counts from a different project.

Cause: known cross-project leak in the header counter aggregation. Tracked as `bug-board-shows-wrong-project-counter-cross-project-leak` (currently in `5-human-review`).

Workaround: refresh the page; the counts recompute from `/api/tasks/grouped` on load. If the leak persists across refreshes, capture the response payload and attach it to the existing job rather than opening a new bug.

## "Two jobs sitting in 3-progress at the same time"

Symptom: the lane that is supposed to hold one job at a time briefly (or persistently) shows two cards.

Cause: the auto-review reissue race documented in [../../.agents/skills/task-api/references/known-pitfalls.md](../../../.agents/skills/task-api/references/known-pitfalls.md) §5. When the runner moved a job to `4-auto-review` and the orchestrator decided "reissue" while a fresh job was being picked up, both ended up in `3-progress`.

Status: fixed by `fix-auto-review-reissue-must-go-to-ready-not-progress` (2026-05-11). Reissues now land in `2-ready order=0` instead of `3-progress`. If you still see two cards in `3-progress` after that date, inspect each job's `cli-output.log` for `Decision: reissue` to confirm whether the fix regressed or you are hitting a different race.

## "Aspect-runner says Concerns without a reason"

Symptom: a job's auto-review aspects all read `Concerns: Aspect runner produced no parseable verdict` with no body.

Cause: pre-2026-05-11 bug. The aspect-runner CLI invocation used `-p <multi-KB prompt>` as argv on Windows, which silently failed (argv-length overflow). All four aspects defaulted to the same template.

Status: fixed by routing the aspect-runner through `ICliOneShot` (stdin-piped). Aspects produced after 2026-05-11 should carry real verdicts. When triaging the lingering 100+ jobs in `5-human-review` from that day, filter aspect rows whose summary matches `/Aspect runner produced no parseable verdict/i` and treat them as no-signal.

## "PUT /api/runner/<project>/mode returns 400"

Symptom: enabling `auto-continuous` for a newly-added project returns `400 Invalid project or mode`.

Cause: `TaskRunnerService` only creates per-project runners at startup. Hot-reload of `WatchPaths` makes the project visible to `/api/watch-paths` but doesn't register the runner.

Fix: restart the backend (`./api.sh restart`). Tracked for a durable fix as `fix-runner-mode-rejects-newly-added-projects`. Full context: [onboard-a-project.md](./onboard-a-project.md) Step 2.

## "api.sh restart said it worked, but the old code is still being served"

Symptom: `./api.sh restart` prints `API stopped.` and then `API is successfully started and healthy!`, but the deployed change is not live. A config edit (for example a `project-settings.json` patch) has no effect, the process start time in `./api.sh status` is older than the restart, or a rebuild fails to copy into `backend/bin` because a file is locked.

Cause: this was a real defect, fixed on 2026-08-24 (AGT-2678). Before the fix, `stop` only signalled the one PID it happened to know about, and `start` only asked whether `/healthz` returned 200. Neither question can tell the new process from the old one:

- `dotnet run` is a launcher whose *child* owns the port. Signalling one of the two left the other alive. A surviving child kept serving; a surviving launcher kept the build output locked, which is what produced the "zombie PID holds the DLLs" rebuild failure.
- An orphaned backend answers `/healthz` exactly like a fresh one. When the old process still owned the port, the newly launched one died with an address-in-use error and `start` reported the *old* process as its own success.
- Nothing ever compared the process before the restart with the process after it.

Fix: nothing to do on a current checkout. `api.sh` now verifies each step and fails loudly instead of reporting a hollow success: `stop` sweeps the port listener and every `OrchestratorApi` process of that checkout, then proves the port is free; `start` refuses a port owned by a process it did not launch; `restart` asserts that the PID and the process start time both changed.

To prove it on your own machine, or to check an older checkout:

```sh
bash tools/api-restart-selfcheck.sh                                    # hermetic, no .NET build needed
bash tools/api-restart-selfcheck.sh --live ../agent-taskboard-stable   # restarts the real seat
```

The hermetic run reproduces both failure shapes (an orphan that outlived its launcher, and a healthy stranger on the port) and fails with a named check against any `api.sh` that does not satisfy the contract. Full analysis: [../common-problems/hollow-api-restart/](../common-problems/hollow-api-restart/).

## "Codex run lands as missing-terminal-sentinel"

Symptom: a Codex job finishes the work cleanly but the run is marked `missing-terminal-sentinel` and lands in auto-review instead of `4-auto-review -> 5-human-review` with a clean Done.

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

## "Crash recovery committed my uncommitted edits"

Symptom: you ran `./api.sh start` while you had uncommitted edits in the dev checkout; on boot a `chore(crash-recovery)` commit appeared with your work folded in.

Cause: the crash-recovery service auto-commits uncommitted edits on boot to keep the runner's commit boundary clean. This is the documented behaviour but it's surprising the first time.

Fix going forward: commit before booting the dev backend. The orchestrator memory entry "Crash recovery auto-commits before you can" captures the reminder.

## "I want to script a job move but the API rejects it"

Symptom: `POST /api/tasks/<id>/move` returns `409 Job already exists or invalid input` even though the slug is unique.

Cause: you passed `rootPath` as `watchPath`. The server resolves jobs against the *resolved job-folder root* under the workspace, not the project's source tree.

Fix: use the `path` field returned by `GET /api/watch-paths`, not `rootPath`. The full pitfall and ready-to-use Node templates live in [../../.agents/skills/task-api/SKILL.md](../../../.agents/skills/task-api/SKILL.md).

## "Claude quota panel is empty / plan shows unknown"

Symptom: the Claude quota widget (status bar donut, admin panel) shows no usage windows and no plan, even though `claude` works fine when you run it yourself.

Cause: the backend's quota probe drives `claude` headlessly and sends `/usage`. If the CLI has never been taken past its first-run onboarding (folder-trust dialog, theme picker, a "try the new renderer" upsell), those screens sit in front of the ready REPL and swallow the `/usage` command, so the probe returns zero windows. `ClaudeQuotaProbe.LooksLikeOnboardingWizard` (see [ClaudeQuotaProbe.cs](../../../backend/Features/Cli/Quota/ClaudeQuotaProbe.cs)) detects this case and logs it distinctly from an actual `/usage` output-format drift.

Fix: run `claude` interactively yourself once, log in, and click through every first-run screen until you reach the normal `? for shortcuts` prompt. The onboarding state persists in `~/.claude.json`, so this is a one-time fix per machine/user. See [getting-started.md](./getting-started.md) step 1 for the full first-install version of this.
