# In Progress

The `3-progress` lane holds jobs that the orchestrator is actively running, or has been running and intends to resume. Cards here are not "queued"; they are either streaming output right now or paused between attempts. The orchestrator picks up work strictly oldest-first within this lane before it ever looks at `2-ready`, so a stuck card here holds the project's run slot. By default a project has one slot and runs one task at a time; a project with parallel slots (`maxParallelism`) runs one task per slot.

## What an active run looks like

When you click into a card in this lane, the protocol view is the live activity log. The CLI agent's stdout streams into `logs/cli-output.log`; the orchestrator's typed messages (decisions, reissues, heuristic verdicts, give-ups) interleave on the `[orchestrator]` stream. The watchdog tracks the run through a small set of phases: starting, streaming, idle, and exited. An idle phase means the agent has been silent for longer than the configured threshold; the watchdog will keep waiting up to a hard ceiling before it forces the run down.

## How to read the activity log

Each line carries a participant: `you`, the agent name, or `[orchestrator]`. The post-run sentinel (`[[TASK_DONE]]`, `[[TASK_BLOCKED:...]]`, `[[TASK_NEEDS_INPUT:...]]`, `[[TASK_NOOP]]`) is what moves the card off this lane. Without a sentinel the analyzer falls back to a heuristic and surfaces a meta message so you can see the verdict was a guess rather than authoritative.

## What to do when the lane stalls

A card that has been silent here for many minutes is usually one of three things: the CLI is genuinely thinking; the CLI is wedged behind a quota or auth wall; or the previous attempt died before it ever streamed output. The third case is the most restartable one: after the configured number of empty attempts (default 3) the pickup loop reroutes the task itself instead of jamming the lane. Per ADR-0051 there is no dead-letter lane — a CLI that never spawned sends the task back to `2-ready` and pauses the runner; a CLI that ran but stayed silent escalates the task to `5-human-review`. For the first two cases, the supervisor's pause / resume / cancel primitives are the right tool, not folder edits. Moving cards by hand here will fight the runner.

## Reference

- ADR-0028 (strict-iteration progress-first pickup; dead-letter destination superseded by ADR-0051)
- ADR-0030 (CLI watchdog phases)
- ADR-0051 (eliminate the failed-pickup lane)
