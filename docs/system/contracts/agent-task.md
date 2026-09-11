# Agent Task Contract

agent-orchestrator owns the queue. A CLI agent owns only the task slot that the application starts.

This contract is copied into watched target projects so Claude Code, Codex, GitHub Copilot, Gemini, and other coding agents understand the boundary.

## Controller Boundary

The application owns:

- Selecting the next ready task.
- Moving task folders between state lanes.
- Starting, stopping, and continuing CLI runs.
- Enforcing per-project slot admission (`maxParallelism`), including the default one-active-coding-task behavior.
- Recording CLI execution state, session ids, logs, summaries, and review transitions.
- Preparing, reusing, and tearing down task worktrees and reissuing tasks without losing uncommitted or unpushed work (see Worktree Lifecycle Safety Invariant below).

The agent owns:

- Reading the selected task's prompt.md.
- Implementing the requested change in the project source tree.
- Reading existing task evidence when resuming or recovering.
- Writing review evidence such as screenshots, result files, or short notes when project instructions require it.

### Worktree Lifecycle Safety Invariant

The platform owns the commit boundary (agents leave git to the application; see the commit-push-doctrine), so it also guarantees that tearing down a worktree or reissuing a task never destroys uncommitted or unpushed task work:

- Before removing a worktree, the platform snapshots any dirty or untracked changes onto the task's `task/<id>` branch as a platform WIP safety commit, and refuses teardown if that snapshot fails rather than force-removing the deliverable. A merged-ancestor ("already folded into develop") check alone is not sufficient: a failed auto-commit leaves the branch tip at develop while the real work lives only as dirty files in the worktree (AGT-1945).
- Committed work that is not yet merged into the integration branch keeps the worktree. Terminal teardown only proceeds when `task/<id>` is a strict ancestor of develop, so an unmerged attempt is preserved for a reissue or human review.
- A genuine failure to land agent edits onto the task branch at integration is surfaced as a High `integration-error`, never silently swallowed on the happy path.

This is why an agent may safely end a run with work left uncommitted: durably preserving it is the platform's responsibility, not the agent's. The runner-side mechanics live in `docs/system/domains/runner.md` and the incident log in `docs/concepts/orchestrator-drive-to-conclusion.html`.

## Agent Rules

When a CLI run starts, the application has already selected the task.

Agents must:

- Work on exactly the task they were given.
- Read the task prompt from the path provided by the runner.
- Ask one precise question before working when the task or its reference is genuinely ambiguous; do not guess. Use `[[TASK_NEEDS_INPUT:<short reason>]]` for that turn.
- Treat the job folder as task-local evidence and context.
- Work in the project source tree for implementation changes.
- Keep screenshots that matter for review under the job folder's results/ directory.
- Preserve existing work when resuming or recovering a task.

Clarification is deliberately narrow. Ask when a reference is unclear (for
example, "change that one"), instructions conflict, or the requested outcome is
missing. A clear task should proceed immediately without a confirmation
question. Missing dependencies and environment failures remain blocked
conditions; they are not clarification turns.

Agents must not:

- Scan for other ready tasks.
- Pick another task.
- Move task folders between state lanes.
- Edit the `state` or `phase` fields in job.json, or write to `lifecycle.json`. These are application-owned.
- Start or continue another task on their own.
- Create branches, switch branches, merge branches, or manage worktrees. When parallel mode is enabled, branch and worktree lifecycle is still application/pipeline-owned.

## State Model

The visible task states are (ADR-0025: three-stage review pipeline):

```text
1-preparation -> 2-ready -> 3-progress -> 4-auto-review -> 5-human-review -> 6-completed -> 7-archive
```

State transitions are application-controlled. A task can sit in `3-progress` without a live CLI process after a stop, crash, or backend restart. Treat the live CLI execution state as the real signal for whether work is currently running.

Only successful CLI runs move automatically from `3-progress` to `4-auto-review`. The durable state key stays `4-auto-review` for compatibility, but the visible board lane is Post Processing. The orchestrator's review-decision pass then reissues (back to `3-progress`), accepts-as-done (forward to `5-human-review`), or escalates (also forward to `5-human-review`). The user always confirms the move from `5-human-review` to `6-completed`. Failed or stopped runs stay in `3-progress` so the user can inspect the log, restart, or continue the task.

## Task Files

Each task folder may contain:

- `job.json`: metadata owned by the application. The optional `phase` field carries an orchestrator-driven substate (Intake, Post Processing); agents do not write it. The optional `acceptanceScope` field is the authoritative requirement-fit boundary. A `bounded-slice` scope names one slice and its complete acceptance criteria; broader parent or Dossier work is outside this delivery.
- `prompt.md`: task description and follow-up notes.
- `status.md`: generated review protocol owned by the application.
- `lifecycle.json` (optional): application-owned sidecar with richer phase history. Absent on legacy folders.
- `post-processing-outcomes.jsonl` (optional): append-only evidence rows for the orchestrator-owned Post Processing phase.
- `logs/cli-output.log`: durable CLI output.
- `attachments/`: input images or files supplied with the task.
- `results/`: output screenshots or files produced during the task.

Agents may read all task files. Agents may write evidence files when useful, but must not change queue state. Do not rely on hand-written `status.md` content for durable evidence because the application may regenerate it from logs.

`post-processing-outcomes.jsonl` rows use the typed outcomes `pass-to-human-review`, `findings-added`, `needs-follow-up-task`, `needs-human-input`, and `failed-post-processing`. Each row records the performer (`orchestrator`, `supporting-agent`, or `tool`) and may include `performerCliType` when a supporting CLI such as Claude, Codex, Copilot, or Gemini performed the check. This identity is evidence only. It does not transfer source-editing authority to the supporting agent.

## Skill Lookup

agent-orchestrator may manage reusable standard skills and project-specific skills centrally. A watched project should expose a small README or agent-instruction section that points direct CLI agents to the relevant skills.

This lookup section is for discoverability only. It does not transfer lifecycle ownership to the agent:

- The application still owns queue state, task pickup, stop, continue, review movement, and summaries.
- Skills may explain specialist workflows such as Playwright verification, security review, or project conventions.
- Skills must not ask agents to move job folders, edit task state, or start other tasks.

## Output Contract (machine-read by the orchestrator)

The orchestrator parses CLI output for typed signals so it can decide what to do next without re-reading the agent's prose. Treat these as a hard contract, not a suggestion.

End every run with exactly one of these tokens on its own line:

- `[[TASK_DONE]]` - the work the user asked for is fully complete.
- `[[TASK_BLOCKED:<short reason>]]` - you cannot proceed; explain why briefly.
- `[[TASK_NEEDS_INPUT:<short reason>]]` - you need user input to continue.
- `[[TASK_NOOP]]` - you intentionally did nothing (rare; explain why).

Do not paraphrase the tokens or wrap them in code fences. Multiple tokens in a single run are not allowed; the orchestrator treats only the last one as authoritative.

For `TASK_NEEDS_INPUT`, put the complete operator-facing question immediately
before the token. Include the options you considered, your assessment, and the
specific answer that will unblock the run. The runner preserves that final
assistant text verbatim up to 16 KiB; the short token reason remains an index
slug, not a substitute for the question.

`[[TASK_NOOP]]` is a **recoverable signal, not a terminal state**. When a job lands in `4-auto-review` ending in NOOP, the orchestrator inspects the task and decides deterministically:

- If the task title and prompt body are real (non-empty, non-placeholder) and the per-job reissue budget has not been exhausted, the orchestrator reissues the task to `2-ready` at order 0 (the runner picks it as the very next task without displacing whatever is currently in `3-progress`) with a sharpened framing built from `RunOutcomePolicy.BuildReissueFollowupPrompt` and writes it as `orchestrator-follow-up.md`. The card is also stamped with the `reissue:autoreview` tag so the kanban can highlight it distinctly from a fresh queued task.
- If the title or prompt is empty / placeholder, the orchestrator promotes the task to `5-human-review` with a `[supervisor] [escalate]` chat-note and creates a `human-decision-needed-<slug>` intake in `1-preparation`.
- If the task has already passed the reissue budget (default 2, shared with NEEDS_INPUT-driven reissues so the agent never sees double-spend), the orchestrator escalates the same way.

The NOOP branch is fully deterministic - no fast-model CLI call, no per-hour rate consumption.

When no token is emitted, the orchestrator falls back to a heuristic, marks the verdict as fallback, and posts an `Orchestrator` meta message into the chat so the user can see that the deterministic contract did not match.

When you receive a recovery prompt (the previous CLI session was lost), the user follow-up is the primary instruction. Do not reply "task done" without performing the follow-up; if you cannot perform it, emit `[[TASK_BLOCKED:<reason>]]`.

## Build-time Observability

Coding agents should consider practical build-time observability whenever their change introduces or alters meaningful product behavior. This is guidance, not a checklist; many tasks need none of it.

Apply when relevant:

- Preserve existing structured logs and event names. Do not silently delete instrumentation while editing nearby code.
- For new meaningful behavior, emit structured logs or domain events with stable event names and useful error context, and add timing around expensive or user-visible paths when it would help future debugging, performance review, or QA.
- Use the project's existing logging conventions and field names. Do not invent a parallel logging style.

Do not apply when the change is trivial:

- Tiny helpers, pure refactors, doc-only edits, dependency bumps, and throwaway scripts do not need new instrumentation.
- Observability is not a reason to bloat simple code or pad a diff. If a single log line would not help any future reader, skip it.

The project-level observability contract (event envelope, sinks, correlation rules) lives in `docs/operations/runtime/observability.md` once that file exists. Until then, follow the project's current logging conventions.

## Documentation Drift

After a CLI-executed task finishes, check whether README.md, ROADMAP.md, AGENTS.md, or docs need to be updated. Update them when the change affects product direction, public behavior, architecture, CLI contracts, filesystem contracts, or agent workflow. If no documentation update is needed, say so briefly in the task report.
