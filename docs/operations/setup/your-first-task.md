# Your first task

Studio opens **Settings → First-run checklist** after owner sign-in when the
Task Server has no active project. The checklist remains available in Settings.
Its Refresh button rereads installation identity, project repository
registration, host heartbeats, provider authentication capabilities, and the
latest admitted repository proof. A failed read stays pending. Once those
checks pass, **Create sample card** queues a small overview-document task in
the first registered project. Studio follows that card by its id and marks the
proof complete only after coding, review, and integration evidence is visible.
The browser stores the card id, project id, and task-store path for returning
to the checklist; it stores no credential. A local project uses its existing
checkout; a networked project uses its registered origin and the runner's
checkout. Use
[onboard-a-project.md](./onboard-a-project.md) to prepare the checkout.

Once a project is attached ([onboard-a-project.md](./onboard-a-project.md)) and at least one CLI is installed ([onboard-an-agent-cli.md](./onboard-an-agent-cli.md)), queue a small first task to confirm the loop works end to end. The goal is to watch a real run: pickup, CLI invocation, log streaming, lane transition, auto-commit, auto-review.

## A good first task

Pick something **small**, **scoped**, and **read-then-write**: the agent has to look at real files and produce a real diff. This exercises the load-bearing surfaces (Git status, prompt rendering, model selection, sentinel emission, lane transition, commit stamping) without making you wait 20 minutes for a real feature.

The pattern that has worked well on every new project so far is the **Project Overview Doc**:

> Title: `docs: add Project Overview doc`
>
> Prompt:
> ```
> Read the repository's existing README and top-level folders to understand
> what this project is. Write a short overview at docs/project-overview.md
> covering:
> - what the project does (1 paragraph)
> - the main folders and what lives in each
> - how to run it locally (commands only, no setup theory)
>
> Keep it under 80 lines. End your reply with [[TASK_DONE]].
> ```
> Type: `Chore` &middot; State: `2-ready` &middot; CLI: whichever you want to test

What you should see:

1. The card appears in `2-ready`. The runner picks it up within ~5 s in `auto-continuous` mode (or you click Start in `manual` mode).
2. The card moves to `3-progress`. The Activity Log streams tool calls live: `Read`, `Glob`, `Grep`, then `Write`.
3. The agent emits `[[TASK_DONE]]`. The runner records the lane-transition commit (when `AutoCommit` is on), moves the card to `4-auto-review`, and the orchestrator decides accept / re-issue.
4. The card lands in `5-human-review` for your final sign-off. The protocol view shows the prompt, the activity log, the per-run summary, and the commit.

## Anti-patterns

Tasks that look small but make the loop tell you nothing:

| Don't queue | Why |
|---|---|
| `was-ist-1-plus-1` / single-line answers | The agent answers in chat and exits without touching a file. No diff, no commit, no real signal that the loop works. |
| "Refactor module X" without a brief | Open-ended scope; agent flails, the run is long, the failure mode is unclear. |
| "Run all tests and report" | The agent doesn't know the test runner's quirks on day one; the run produces a noisy log without exercising the write path. |
| Anything that requires non-trivial repo conventions | Day-one tasks should not double as onboarding for the agent. Pick something that works from a cold read of the README. |

## Where to watch

- **Board view** (`http://localhost:4010`) shows the card moving lanes. Drag-and-drop is optimistic with a snapshot-revert path.
- **Detail panel** (click the card): prompt, status, live activity log with tool-call ticker, live `git diff` once the agent starts writing.
- **Run timeline**: one card per CLI invocation between user inputs. Useful when a job has been continued multiple times.
- **`logs/cli-output.log`** inside the job folder: the raw CLI stdout/stderr buffer. Look here when the UI parser drops something you saw the model say.

## Creating tasks programmatically

When you need to script task creation (bulk seed, follow-up batches, triage scripts), use the Task API rather than the create dialog. The full contract, the `watchPath` quirk, the `X-Client-Id` header, and ready-to-use Node templates live in [../../.agents/skills/task-api/SKILL.md](../../../.agents/skills/task-api/SKILL.md).

A minimal create looks like:

```js
// scripts ship in .agents/skills/task-api/scripts/
const watchPaths = await fetch('http://127.0.0.1:5030/api/watch-paths').then(r => r.json());
const target = watchPaths.find(w => w.name === 'Lotta Dashboard');

await fetch('http://127.0.0.1:5030/api/tasks', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json', 'X-Client-Id': 'local-default' },
  body: JSON.stringify({
    watchPath: target.path,   // resolved job-folder root, NOT rootPath
    title: 'docs: add Project Overview doc',
    promptMarkdown: '...',
    agent: 'claude',
    cliType: 'claude',
    targetState: '2-ready',
  }),
});
```

The Task API skill is **mandatory** before any scripted board mutation; the `watchPath` vs. `rootPath` distinction trips up every new operator at least once. Keep `agent` and `cliType` on the same real CLI value (`claude`, `codex`, `copilot`, or `gemini`).

## What "the loop works" actually means

If you can queue the Project Overview task, watch it pass through `2-ready -> 3-progress -> 4-auto-review -> 5-human-review`, and read the resulting `docs/project-overview.md` in the diff pane, the install is healthy. From there, scale up: queue real features, set `auto-continuous`, walk away.

If something goes sideways - the card pauses, two cards end up in `3-progress`, the counters look wrong, the agent emits only sandbox errors - check [troubleshooting.md](./troubleshooting.md) before opening a bug.
