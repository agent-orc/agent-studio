# Loop Inventory

Every place in the codebase where work can re-enter itself — retry, requeue, re-trigger, replay, recurring tick — is registered here. The inventory is the single source of truth for the loop-guard layer documented in [agent-contract-pattern.md](./agent-contract-pattern.md) and decided by [ADR-0032](../architecture/decisions/adr-archive.md).

When you add a new loop, the same commit must add:

1. An entry below in **Entries** with all fields filled.
2. A budget constant in code (named in the entry's `Budget` field).
3. A breaker test under `backend.Tests/Architecture/` (named in the entry's `Breaker test` field).

CI fails if any entry is missing one of the three. The check runs in `backend.Tests/Architecture/LoopInventoryConsistencyTest.cs` (every CI run, fast, no LLM).

A weekly LLM-driven discovery run (`LoopDiscoveryTest`, `[Trait("Category","Weekly")]`, gated on `LOOP_DISCOVERY=1`) scans the diff since the last green run for new candidate loops and writes proposals to `loop-inventory.md.candidates`. Proposals are committed by a human review, never auto-applied.

## Entry shape

Each entry uses the same fields:

```markdown
### <stable-id>

- **Kind:** Pre-Guard | Post-Guard | Tick | Watchdog
- **Where:** <file path or class.method>
- **Re-entry trigger:** what causes this loop to attempt to fire again
- **Budget:** named constant + default value (e.g. `PickupFailureThreshold = 3`)
- **Action when budget exhausted:** what the rule engine does
- **Breaker test:** path to the synthetic test that exercises the budget
- **Last fired:** ISO date + one-line context
- **Notes:** anything operator-relevant (e.g. counter persistence, reset rules)
```

`<stable-id>` is dotted: `<surface>.<what>` (e.g. `pickup.silent-runs-per-job`). Once published, the id is stable — never rename.

## Entries

### pickup.silent-runs-per-job

- **Kind:** Pre-Guard
- **Where:** [`backend/Services/Runner/ProjectRunner.cs`](../../backend/Services/Runner/ProjectRunner.cs) `TryPickProgressJobOrDeadLetter` + `RecordPickupAttemptResult`
- **Re-entry trigger:** Pickup tick attempts to spawn the CLI for the same slug after a previous attempt produced zero stdout lines.
- **Budget:** `PickupFailureThreshold` (default 3 silent attempts per slug)
- **Action when budget exhausted:** Reroute the over-budget folder via `ProjectRunner.RerouteOverBudgetFolder` and append a row to `<workspace>/logs/pickup-failures.jsonl`. Per ADR-0051 there is no `3a-failed-pickup` dead-letter: a spawn failure (CLI never started) returns the task to `2-ready` and pauses the runner; a task-shaped failure (CLI ran but stayed silent) escalates to `5-human-review` and the runner continues.
- **Breaker test:** `backend.Tests/Architecture/PickupSilentRunsBreakerTest.cs` (to be added with the implementation task)
- **Last fired:** 2026-05-06 — 22-job drain caused by a 500-byte `claude.exe` stub from a broken npm postinstall. Detection worked correctly; the gap was the missing diagnosis step (now `pickup.diagnose-once-per-dead-letter`) and the missing cross-slug circuit breaker (`pickup.cross-slug-infra-circuit-breaker`).
- **Notes:** Silent run is defined as zero stdout lines, regardless of stderr or exit code. Stderr-only failures look identical to genuinely-quiet sessions; the diagnosis step disambiguates after the fact.

### pickup.cross-slug-infra-circuit-breaker

- **Kind:** Pre-Guard (cross-slug)
- **Where:** [`backend/Services/Runner/CrossSlugInfraCircuitBreaker.cs`](../../backend/Services/Runner/CrossSlugInfraCircuitBreaker.cs); fed by `ProjectRunner.DeadLetterUnrecoverableFolder` (trip) and `ProjectRunner.RecordPickupAttemptResult` (productive-pickup reset); operator-resume reset hooked from `TaskRunnerService.SetMode`.
- **Re-entry trigger:** Two consecutive distinct slugs hit `pickup.silent-runs-per-job` for the same `(cliType)` within a sliding window.
- **Budget:** `CrossSlugInfraSilentLimit` (default 2 distinct slugs in 10 minutes for the same CLI). Configurable via `Supervisor:CrossSlugInfraSilentLimit` and `Supervisor:CrossSlugInfraSilentWindowMinutes`.
- **Action when budget exhausted:** Set the project's runner mode to `manual` (via the same `SetMode` path the API uses), append one row to `<workspace>/logs/infra-halts.jsonl`, emit one `[supervisor]` chat note on the freshly dead-lettered job, and halt the in-flight pickup tick mid-iteration so the remaining `3-progress` folders are NOT dead-lettered too. The diagnosis step (`pickup.diagnose-once-per-dead-letter`) still runs on the dead-letters that already happened.
- **Reset:** counter clears on (a) operator flip back to `auto-single` / `auto-continuous`, (b) any productive pickup (≥ 1 streamed CLI output line) on the same `(project, cliType)`, (c) 24 hours of inactivity (long-window cleanup).
- **Breaker test:** [`backend.Tests/Architecture/CrossSlugInfraCircuitBreakerTest.cs`](../../../backend.Tests/Architecture/CrossSlugInfraCircuitBreakerTest.cs), plus the unit suite in [`backend.Tests/CrossSlugInfraCircuitBreakerTests.cs`](../../../backend.Tests/CrossSlugInfraCircuitBreakerTests.cs) and the runner-integration coverage in `PickupLoopStrictIterationTests.CrossSlug_*`.
- **Last fired:** Not yet fired in production. The 2026-05-06 incident is the motivating example (would have stopped the drain at job 2 of 22).
- **Notes:** This is the loop guard the 2026-05-06 incident motivated. It deliberately distinguishes "this one job is broken" (per-slug counter, fine) from "the CLI itself is broken" (cross-slug counter, halt). Detection is deterministic: typed counter, config constants, single-state-machine mode flip - no LLM in the code path (ADR-0032).

### pickup.diagnose-once-per-dead-letter

- **Kind:** Pre-Guard (within the contract pattern's own Pre-Guard)
- **Where:** Diagnosis dispatcher invoked from `JobStateMachine.MoveFolderToFailedPickup` (to be added)
- **Re-entry trigger:** A job's dead-letter triggers a `pickup-failure-diagnosis` agent step. The Pre-Guard prevents a second diagnosis on the same slug, and across-slug rate limits prevent runaway agent invocations.
- **Budget:** `DiagnosisAttemptsPerSlug = 1`, `DiagnosisAttemptsPerHourPerWorkspace = 8`, `DiagnosisTokenBudgetCents = 50` per attempt
- **Action when budget exhausted:** Skip the diagnosis, write a `pickup-diagnosis-skipped` marker to the run folder, leave the dead-letter as-is (operator-resolved).
- **Breaker test:** `backend.Tests/Architecture/PickupDiagnoseBudgetTest.cs` (to be added)
- **Last fired:** Not yet implemented.
- **Notes:** A repeatedly-failing diagnosis is itself an infrastructure problem. We never call a second LLM to diagnose why the first one failed.

### pickup.requeue-after-diagnosis

- **Kind:** Post-Guard
- **Where:** Decider in `backend/Services/Pickup/PickupDecisionService.cs` (to be added)
- **Re-entry trigger:** Decider chooses an action that puts the slug back into `2-ready` (`task-bad-prompt` requeue, `task-env-missing` self-heal-and-requeue, `transient` retry).
- **Budget:** `RequeueAfterDiagnosis = 1` per `(slug, category)` per 24 hours; counter persisted to `<workspace>/state/loop-counters.json` (atomic-rename writes).
- **Action when budget exhausted:** Override the decider's chosen action to `escalate-human`; surface a banner with the per-slug counter contents.
- **Breaker test:** `backend.Tests/Architecture/PickupRequeuePostGuardTest.cs` (to be added)
- **Last fired:** Not yet implemented.
- **Notes:** Counter does not auto-reset on backend restart. The 24-hour window starts at the first fire, not at midnight.

### abort-review.rerun-per-job

- **Kind:** Post-Guard
- **Where:** [`backend/Services/Runner/PostAbortReview.cs`](../../backend/Services/Runner/PostAbortReview.cs) (`PostAbortReviewDecider.Decide`), invoked from `ProjectRunner.OnCliFinished` on a non-clean run end and surfaced through `PostAbortReviewStepService`.
- **Re-entry trigger:** A CLI run ends non-clean (watchdog timeout, non-zero exit, unexpected stop). The abort-review agent judges the abort illegitimate and recommends `rerun` or `stronger-reissue`, which re-spawns the CLI for the same job instead of escalating.
- **Budget:** `PostAbortReviewDecider.DefaultRerunBudget` (default 2 automatic reruns per job).
- **Action when budget exhausted:** `PostAbortAction.EscalateHuman` — route the job to `5-human-review`. A null/unparseable verdict (CLI failure) fails closed to the same escalation regardless of remaining budget.
- **Breaker test:** [`backend.Tests/Architecture/AbortReviewRerunBreakerTest.cs`](../../../backend.Tests/Architecture/AbortReviewRerunBreakerTest.cs)
- **Last fired:** Not yet implemented in production (step is default-OFF per project via `PipelineStepConfigResolver.IsEnabled(settings, PipelineCatalogue.AbortReviewStep)`).
- **Notes:** The decider is pure (ADR-0032): the agent only classifies (`legitimate`, `recommendation`, `confidence`, `reason`); the binding action is computed in code from the recommendation plus the remaining budget. The budget counts down across reruns of the same job; the terminal state is always escalation, so the loop cannot run unbounded. `accept`/`human-review` recommendations bypass the budget (accept continues, human-review escalates immediately).

### completion.retrigger-transient-abort-per-job

- **Kind:** Post-Guard
- **Where:** [`backend/Services/Runner/CompletionRetrigger.cs`](../../backend/Services/Runner/CompletionRetrigger.cs) (`CompletionRetriggerDecider.ShouldRetrigger`), invoked from `ProjectRunner.OnCliFinished` after the abort-review check and before the human-review escalation.
- **Re-entry trigger:** A CLI run ends with a transient process abort (`RunIssueKind.WatchdogTimeout` or `RunIssueKind.InfraCrash` -> `OutcomeActionKind.NotifyUserAndStop`). Instead of dead-ending the task in `5-human-review`, the completion loop re-spawns the same job with its unchanged model. Fires only when the LLM abort-review step (default-OFF per project) did NOT handle the run, so for most projects this is the only completion loop.
- **Budget:** `CompletionRetriggerDecider.DefaultBudget` (2 watchdog re-triggers) or `InfraCrashBudget` (exactly 1 hard-crash re-trigger before escalation/model switch). Counter `_completionRetriggerUsed` is per `jobId`.
- **Action when budget exhausted:** `ShouldRetrigger` returns false; the runner falls through to the existing human-review escalation (`HumanReviewEscalation.EscalateAsync`, category `WatchdogKill`). The terminal state is always escalation, so the loop cannot run unbounded.
- **Breaker test:** [`backend.Tests/Architecture/CompletionRetriggerBreakerTest.cs`](../../../backend.Tests/Architecture/CompletionRetriggerBreakerTest.cs), plus the unit suite in [`backend.Tests/CompletionRetriggerDeciderTests.cs`](../../../backend.Tests/CompletionRetriggerDeciderTests.cs).
- **Last fired:** Not yet fired in production. ASS-665 (a healthy `ng serve` cold-compile killed by the watchdog as `status=stopped, exitCode=-1`) is the motivating example: with this loop the transient kill self-heals via re-spawn instead of parking in human review.
- **Notes:** The decider is pure (ADR-0032): it only classifies an issue as a transient abort and checks the remaining issue-specific budget; the binding re-spawn + counter + terminal escalation live in `ProjectRunner`. `EnvironmentBlocker` is unrecoverable and `PermissionBlocked` needs a human, so both still route to review. The counter resets when the job leaves the run loop (moved to review on a clean run, or escalated to human review), so the budget measures consecutive transient aborts without an intervening successful completion. It does NOT persist across backend restart. Pairs with the watchdog long-op widening (same feature): the long-op fix removes the false-positive kill in the first place, this loop recovers a genuine transient kill that still happens. The re-trigger prompt asks the agent to narrate during long operations so a legitimate wait stays visibly alive.

### integration.attribution-agent-round

- **Kind:** Post-Guard
- **Where:** [`backend/Features/Pipeline/IntegrationAgentRoundService.cs`](../../../backend/Features/Pipeline/IntegrationAgentRoundService.cs) (`RemoteIntegrationContinuationPolicy` and `IntegrationAgentRoundService`), invoked by `RemoteDeliveryIntegrationCoordinator` after `MergeIntoIntegrationOutcome.AgentRoundRequired`.
- **Re-entry trigger:** Direct merge and mechanical three-way/rerere merge both conflict, then the fallback rebase conflicts or cannot preserve a one-to-one delivery commit mapping.
- **Budget:** `RemoteIntegrationContinuationPolicy.MaxAutomaticAgentRounds` (exactly 2 automatic agent rounds per operator-owned review epoch). Prior firings are counted from durable automatic `integration_recovery_queued` timeline events with the same `attemptEpoch`, including rounds started by immediate integration and the acceptance rail.
- **Action when budget exhausted:** Do not requeue again. Persist the integration failure and let the settled Remote Review move the task to Human Review with `automatic recovery budget used: 2/2` as the park reason.
- **Breaker test:** [`backend.Tests/Architecture/IntegrationAgentRoundBreakerTest.cs`](../../../backend.Tests/Architecture/IntegrationAgentRoundBreakerTest.cs)
- **Last fired:** 2026-08-11, AGT-2563 follow-up. A mechanical rebase changed delivery commit cardinality and correctly refused ambiguous SHA attribution, but the card remained in Review until manual requeue.
- **Notes:** The automatic round saves a `steer` pending intent, retains the prior delivery as superseded history, and queues the original card at the front of Ready. Its prompt lists the structured conflict report's files and prefers merging the integration branch into the delivery branch over rewriting delivery history. An explicit operator requeue opens a new review epoch and therefore a new bounded opportunity. Automatic moves never increment the epoch.

### completion.run-timeout-salvage-continuation

- **Kind:** Post-Guard
- **Where:** [`backend/Features/Runner/RunTimeoutSalvageContinuation.cs`](../../../backend/Features/Runner/RunTimeoutSalvageContinuation.cs) (`RunTimeoutSalvageContinuationPolicy` and `RunTimeoutContinuationService`), invoked from the `POST /api/runner/completion` escalation branch in `backend/Features/Tasks/LeaseEndpoints.cs`.
- **Re-entry trigger:** A remote run reports the non-terminal outcome `Unknown` (the observed cause is a hit run timeout) and its completion carried a salvage branch plus salvage commit SHA.
- **Budget:** `RunTimeoutSalvageContinuationPolicy.MaxAutomaticContinuationRounds` (exactly 1 automatic continuation round per delivery generation). Prior firings are counted from durable `continuation_round_started` timeline events with `automatic=true` and the same `attemptEpoch`.
- **Action when budget exhausted:** Escalate to `5e-escalated` exactly as before the loop existed, with the salvage ref, the salvage SHA, and the spent round count folded into the escalation reason by `RunTimeoutSalvageContinuationPolicy.ComposeEscalationReason`.
- **Breaker test:** [`backend.Tests/Architecture/RunTimeoutContinuationBreakerTest.cs`](../../../backend.Tests/Architecture/RunTimeoutContinuationBreakerTest.cs), plus the unit suite in [`backend.Tests/RunTimeoutSalvageContinuationTests.cs`](../../../backend.Tests/RunTimeoutSalvageContinuationTests.cs).
- **Last fired:** 2026-09-17, AGT-2858 (run `run_f419c075`, salvage `521fd1e3`) and AGT-2859 (salvage `f0915674`). Both timed out at the 90-minute run budget with buildable salvaged work and were escalated without a continuation; an operator had to read the journal for the SHA and hand-write the finishing prompt.
- **Notes:** The round is task-server-owned; runner salvage behaviour is unchanged. It saves a `steer` pending intent, appends the finishing instruction to `prompt.md` (the text the remote runner fetches verbatim), pins the round to a clean CLI context because a salvaged worktree leaves no resumable session, and queues the card at the front of Ready under the completing attempt's authority write. A terminal agent statement (`Blocked`, `NeedsInput`) and an unverified delivery never open this loop; the latter keeps its own bounded retry budget in `RemoteDeliveryFailurePolicy`. An explicit operator requeue opens a new review-attempt epoch and therefore a new bounded opportunity.

### ui-task.human-feedback-iterations

- **Kind:** Post-Guard
- **Where:** [`backend/Features/Runner/UiIterationGate.cs`](../../../backend/Features/Runner/UiIterationGate.cs) and `ProjectRunner.HandleUiIterationCompletionAsync`
- **Re-entry trigger:** A human rejects an evidenced UI iteration and Part 2 submits the feedback through the existing Continue flow.
- **Budget:** `UiIterationGate.DefaultMaxIterations` (default 4, configurable per project from 1 through 10 on `PipelineSteps["pre-ui-pipeline-routing"].maxIterations`). Missing evidence has the separate `RunOutcomePolicy.MaxAutoReissueAttempts` budget of 1 retry for the same iteration.
- **Action when budget exhausted:** A feedback Continue at the cap is refused before CLI admission and routed through `HumanReviewEscalation` to `5e-escalated` with category `ui-iteration-cap`. Finishing remains available because it does not start another run.
- **Breaker test:** [`backend.Tests/Architecture/UiIterationBreakerTest.cs`](../../../backend.Tests/Architecture/UiIterationBreakerTest.cs), plus [`backend.Tests/UiTaskPipelineTests.cs`](../../../backend.Tests/UiTaskPipelineTests.cs)
- **Last fired:** Not yet fired in production.
- **Notes:** Iteration state is durable in `results/ui-iteration-NNN/` and `steer-pending.json`. Backend restarts do not reset the counter. The generic steer timeout explicitly ignores this human-review marker.

### ui-task.visual-defect-auto-retry

- **Kind:** Post-Guard
- **Where:** [`backend/Features/Runner/VisualQa/VisualQaPolicy.cs`](../../../backend/Features/Runner/VisualQa/VisualQaPolicy.cs) and `ProjectRunner.HandleUiIterationCompletionAsync`
- **Re-entry trigger:** The multimodal visual verdict for a gated AGT frontend delivery is `clear-defect` with one or more named visible defects.
- **Budget:** `VisualQaPolicy.MaxAutomaticDefectRetries` (exactly 1 automatic visual-QA steer round per durable UI iteration evidence history).
- **Action when budget exhausted:** Do not requeue again. Write the second verdict receipt and move the card to Human Review with all screenshots and named defects in the version 2 review marker. Capture or model unavailability also fails closed to this human hand-off without spending a blind retry.
- **Breaker test:** [`backend.Tests/Architecture/VisualQaRetryBreakerTest.cs`](../../../backend.Tests/Architecture/VisualQaRetryBreakerTest.cs), plus [`backend.Tests/VisualQaPolicyTests.cs`](../../../backend.Tests/VisualQaPolicyTests.cs)
- **Last fired:** 2026-08-12, AGT-2654 controlled before/after browser and multimodal probe.
- **Notes:** The model only classifies pixels. Pure policy owns the lane-affecting action. Retry use is counted from `results/ui-iteration-NNN/visual-qa/round-RRR/verdict.json`, so process restart does not replenish it. The steer is appended through the existing continuation-note boundary before any Human Gate marker exists.

### clean-context.retention-sweep

- **Kind:** Tick
- **Where:** [`backend/Features/Cli/Execution/CleanContextRetentionHostedService.cs`](../../../backend/Features/Cli/Execution/CleanContextRetentionHostedService.cs) `ExecuteAsync`, plus opportunistic acquisition in [`cli-hosting/TaskCleanContextStore.cs`](../../../cli-hosting/TaskCleanContextStore.cs) `Acquire`
- **Re-entry trigger:** Backend startup, each retention timer tick, or a local/remote task acquiring a clean CLI home.
- **Budget:** `CleanContextRetentionHostedService.DefaultSweepIntervalHours = 6` and `TaskCleanContextStore.DefaultRetentionDays = 7`; each pass scans only the CLI/task directory depth under the resolved root.
- **Action when budget exhausted:** Delete homes whose last-use marker is older than the retention cutoff, remove empty CLI directories, and stop after the single bounded pass. Per-directory failures are retained and retried by the next scheduled tick or acquisition; there is no immediate retry loop.
- **Breaker test:** [`backend.Tests/Architecture/CleanContextRetentionBreakerTest.cs`](../../../backend.Tests/Architecture/CleanContextRetentionBreakerTest.cs)
- **Last fired:** Not yet observed in production; AGT-2525 introduces the stable store and its retention owner.
- **Notes:** Attempt teardown only refreshes last use. A fresh home whose CLI launch was never adopted is deleted immediately. Ownership shape and task-marker validation prevent the sweep or a continuation from adopting another task's directory.

### local-cli.missing-npm-shim-repair

- **Kind:** Tick
- **Where:** [`backend/Features/Cli/Quota/CliVersionMonitorHostedService.cs`](../../../backend/Features/Cli/Quota/CliVersionMonitorHostedService.cs) and [`backend/Features/Cli/Repair/LocalCliRepairService.cs`](../../../backend/Features/Cli/Repair/LocalCliRepairService.cs)
- **Re-entry trigger:** The startup or periodic local Claude/Codex version probe cannot resolve the configured global command, and inspection finds either that the matching package is absent from the Windows npm global `node_modules` tree or that the package is present while its `.cmd` command shim is absent.
- **Budget:** `LocalCliRepairService.AttemptWindow` (exactly one npm repair attempt per CLI per hour, reconstructed from the durable JSONL journal after restart). A missing package uses `npm install --global <package>`; a missing shim with its package present uses `npm install --global <package> --force`.
- **Action when budget exhausted:** Keep the CLI unavailable and skip the repair until the hour expires. Only a currently active failure raises an operator alarm. Successful repairs and later healthy probes occupy no runner-status UI space.
- **Breaker test:** [`backend.Tests/Architecture/LocalCliRepairBreakerTest.cs`](../../../backend.Tests/Architecture/LocalCliRepairBreakerTest.cs), plus the portable detection and journal suite in [`backend.Tests/LocalCliRepairServiceTests.cs`](../../../backend.Tests/LocalCliRepairServiceTests.cs).
- **Last fired:** 2026-08-13 and 2026-08-18 were manually repaired before this loop existed. Both incidents had the Claude npm package present with all global shims absent; the second repair moved the observed version from 2.1.231 to 2.1.234.
- **Notes:** The journal is `<TaskRepository>/logs/cli-self-heal.jsonl` (or `runtime/cli-self-heal.jsonl` without a task repository). Each attempt captures the package/shim state, selected install or force-relink action, last observed/package version, post-repair CLI version, npm exit and redacted output, package metadata, and nearby npm debug-log activity. npm resolution prefers the installation beside the active Node executable, then verified APPDATA and PATH candidates; every candidate must pass `npm --version` from an explicit working directory before install. No passing candidate produces the typed `npm-unavailable` result. Success requires both a restored `.cmd` shim and a successful CLI `--version` probe. A later healthy probe appends `resolved`, clears the active failure projection, and prevents journal rehydration from restoring it. Custom executable paths and present-but-broken command shims are outside this automatic repair policy.

## Candidates (LLM-proposed, human-reviewed)

This section mirrors `loop-inventory.md.candidates` once the weekly `LoopDiscoveryTest` starts running. Items move from candidates to **Entries** above only after a human review confirms the loop is real and assigns a budget + test. Empty for now.
