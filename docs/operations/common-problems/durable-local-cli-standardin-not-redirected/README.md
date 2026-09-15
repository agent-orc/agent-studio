---
id: durable-local-cli-standardin-not-redirected
title: "Local CLI runs fail at spawn with 'StandardIn has not been redirected' and the card flaps"
status: fixed
first-seen: 2026-09-15T00:00:00Z
last-seen: 2026-09-15T23:59:00Z
severity: blocker
category: cli
tags: [claude, cli, durable-worker, spawn, restart-continuity, runner-mode, windows]
affects:
  - backend/Features/Cli/Execution
  - backend/Features/Runner
related-tasks: [AGT-2780, AGT-2821]
related-adrs: [ADR-0072]
---

# durable-local-cli-standardin-not-redirected

**Symptom.** Every local Claude run fails at spawn with
`System.InvalidOperationException: StandardIn has not been redirected`
(`DurableLocalCliProcessSpawner.Spawn`), and the card cycles between `2-ready`
and `3-progress` for as long as the backend runs. Observed on Stable 0.3.0.

**Cause.** Two independent defects.

1. The durable worker was started with redirected stdin, stdout and stderr, and
   the backend then disposed that `Process` object, which closes the pipes. The
   spawner reopened the worker with `Process.GetProcessById` and handed CAR
   `process.StandardInput` / `StandardOutput` / `StandardError` from a handle
   that never had redirected streams. Claude delivers its prompt through
   `ClaudePromptTransport.Stdin`, so the very first access threw.
2. The per-task spawn budget pauses the runner, but the CLI-recovery probe
   restored auto mode as soon as `claude --version` answered. A CLI binary that
   is healthy while every run still fails to spawn made those two bounded
   mechanisms compose into an unbounded pause/resume loop - the visible card
   flapping.

**Fix (AGT-2821).** The worker directory is the transport: the backend writes
the prompt into `input.bin` and publishes `input.done` with the byte count when
it closes stdin, and it tails live stdout/stderr from the same `output.jsonl` a
replacement backend reads after a restart. The worker's `result.json` is the
exit-code authority, because a reopened process handle cannot report an exit
code on Linux. The CLI-recovery auto-resume is bounded by
`CliRecoveryResumePolicy`: after three restored runs that never start a process
the runner rests in manual and its mode reason says so.

**Operator step after the release.** Hosts that worked around the failure with
`LocalCliDurability:Enabled=false` in the gitignored `appsettings.Local.json`
must remove that entry, otherwise they keep running without restart continuity.

**Diagnosis.** `logs/` shows the spawn exception with
`DurableLocalCliProcess.cs` in the stack, `logs/pickup-failures.jsonl` fills
with `requeued-ready` rows for the same slug, and the runner mode alternates
between `manual` and the operator's auto mode.
