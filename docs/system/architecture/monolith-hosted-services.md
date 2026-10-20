# Monolith hosted services and decision ownership

AGT-3060 (I18) moves the background deciders' rules into the shared decision
library (`orchestration-decisions/`) and makes them occurrences of the AI
Operator role in the Orchestrator Engine (AGT-W65 D14 A, AGT-W73 O1 A). The
operator decision of 15.10.2026 keeps the per-transition execution switch of
AGT-3058/3059 as the single selector of who decides: the default stays
`Monolith`, and with `Monolith` selected the monolith keeps deciding through the
same library. Removing the Monolith mode is a later step, after every profile
runs on `Engine` and is stable.

This inventory lists every hosted service and background handler that
`backend/Host/Program.cs` and `OrchestrationLoopRegistration` register,
classifies it, and names the owner of its decision. Update the table whenever a
hosted service is added, moved or removed. The guard
`backend.Tests/Architecture/MonolithDecisionBoundaryTests.cs` enforces the
classes below and rejects backend construction of transition receipts without
a baseline exception list.

## Classes

| Class | Meaning |
|---|---|
| decide, operator | An AI Operator decider. Its rule is in `AiOperatorDecisionPolicy` of the decision library. The service collects typed facts and submits them through `AiOperatorDecisionGateway`; the `OperatorDecision` switch selects whether the monolith decides in process (`Monolith`, default) or the engine decides (`Engine`). The service applies the decision, or its `IAiOperatorEffect` applies the engine receipt. |
| decide, switched | A service gated by an AGT-3058/3059 transition profile (`CodingCompletion`, `ReviewReport`, `GateResult`, `OperatorSweep`, `OperatorDecision`). `MonolithTransitionSwitch.For` selects the owner. |
| decide, local | The local runner and three supervisor deciders without engine occurrences. OrchestratorPrep, MetaCycle and AutoIntervention keep running on the monolith when `OperatorDecision` selects Engine. The guard keeps this set closed. |
| apply-recover | The service applies an already recorded decision, replays a keyed write, or recovers authority state after a restart. It stays with the task authority until the Task Server cutover (I11). |
| decide, event | A residual decision in a request handler that applies an outcome a runner host reports. It moves with the Task Server planes (I11). |
| report | Diagnostics, alarms, advisories or projections; no lane move. |
| infra | Caches, watchers, Git maintenance, process reaping, retention, sync. |

## Who decides

- `Orchestration:Transitions:<profile>` selects the mode per profile, with the
  rollout journal's operator switch and rollback on top
  (`OrchestrationRolloutStore`) and `Orchestration:ExecutionMode` as the
  fallback. Every profile defaults to `Monolith`. No host forces a mode at
  startup, and an explicitly configured `Monolith` profile is honored.
- The AI Operator deciders share the profile `OperatorDecision`
  (`AiOperatorDecisionGateway.Transition`). On `Monolith` the gateway returns the
  library decision at once and the service applies it. On `Engine` (or
  `Shadow`) the gateway records an `OperatorDecision` occurrence; the engine's
  `AiOperatorDecisionLoop` decides with the same `AiOperatorDecisionPolicy` and
  returns a receipt with the decider, effect, autonomy class and reason. The AI
  Operator acts on A0 and A1, records an A2 action as a proposal for a person,
  and asks a person instead of taking an A3 action. The authority applies an
  Act or Ask receipt through the decider's `IAiOperatorEffect`; each effect
  checks the lane and the frozen facts again before it writes.
- The residual deciders `delivery-chain`, `cause-breaker`, `intake-routing` and
  `acceptance-rail` key their occurrences on a hash of the facts
  (`AiOperatorDecisionGateway.FactsSubjectKey`): unchanged facts map to one
  occurrence, so an engine Hold is not asked again until a fact changes.
- With `OperatorDecision` on `Engine` and the engine stopped, these deciders
  only record pending occurrences and move no card;
  `MonolithOrchestrationRecoveryService` raises the existing engine-unavailable
  alert after one minute. Switching the profile back to `Monolith` is the
  rollback: the next pass decides in process through the same library.
- `OrchestrationEngineReadiness` is a report, never a hold. It records each
  authenticated engine request on `/api/v1/orchestration` and is required only
  while a profile selects the engine. `GET /healthz/orchestration-engine`
  answers `ready` (200), `engine-not-live` (503) or `not-required` (200).
  `OrchestrationEngineReadinessService` logs at startup whether an engine
  identity is enrolled and, after `StartupGraceSeconds` (default 60) without
  contact, raises one operator feed alert per watch path and outage. Readiness
  is not required behind the Task Server plane proxy or on a public-demo host.

## Inventory

Registration abbreviations: `!PD` means not the PublicDemo execution profile;
`loop` means `OrchestrationLoopRegistration` under the loop-host switch
`Orchestration:ExecutionMode=Monolith` and `!PD`.

| Hosted service | Registration | Class | Owner after AGT-3060 |
|---|---|---|---|
| MonolithOrchestrationRecoveryService | always | apply-recover | Authority. Activates prepared occurrences, replays `applying` receipts, alerts on engine outages, and applies the rollout's rollback to the monolith policy. Re-arms the batch admission of a completed engine `DeferToBatch` decision whose card waits in Auto Review without a queue entry or ownership past admission, and clears wait markers that name a finished occurrence. |
| OrchestrationEngineReadinessService | always | report | Authority host. Reports engine readiness and alerts when a profile selects the engine and no engine is live. |
| LinkSupervisor | always | infra | Authority host |
| OrchestratorChatLegacyMigrationHostedService | !PD | infra | Authority host |
| DecisionReminderSweepHostedService | always | report | Authority host |
| DeliveryChainReconciler | always | decide, operator | AI Operator decider `delivery-chain` (`DeliveryChainPolicy`). Effect: `DeliveryChainReconciler` moves the card; a red merge gate is still routed by `GateFailureRouter` before it may park (gate triage, a host rule in the baseline). |
| BatchMoveJobCoordinator | !PD | apply-recover | Authority (applies an operator command) |
| TaskIndexCacheDiagnosticsService | always | infra | Authority host |
| ResultSummaryRetryScheduler | !PD | apply-recover | Authority |
| AttemptArchiveIndexWarmupService | always | infra | Authority host |
| RemoteQueueStarvationWatchdog | !PD | report | Authority host |
| HostReleaseDriftWatchdog | !PD | report | Authority host |
| AutoReviewQueueStagnationWatchdog | !PD | report | Authority host |
| AdaptiveReviewParallelismAdvisor | !PD | report | Authority host (advisory) |
| CodingYieldAdvisor | !PD | report | Authority host (advisory) |
| ReviewInfrastructureRetryScheduler | !PD | apply-recover | Authority. Mints the successor attempt for a retry the review decision already scheduled. |
| CauseBreakerHostedService | !PD | decide, operator | AI Operator decider `cause-breaker` (`CauseBreakerClosePolicy`). Effect: `CauseBreakerService.ApplyCloseReceipt` releases the waiting cards. A green probe closes on the review report path. |
| SystemLoadThrottle | always | infra | Authority host |
| AutoTagCreationWorker | not Test | infra | Authority host |
| AutoTagBackfillQueue | not Test | infra | Authority host |
| TagMaintenanceWorker | !PD | infra | Authority host |
| HardHealthCheckHostedService | !PD | report | Authority host |
| SoftReasoningHostedService | !PD, off by default | report | Authority host |
| AutoInterventionHostedService | !PD, off by default | decide, local | Monolith until an engine occurrence can apply its advisory. |
| MetaCycleHostedService | !PD, off by default | decide, local | Monolith until an engine occurrence can apply its report. |
| OrchestratorPrepHostedService | !PD, off by default | decide, local | Monolith until an engine occurrence can apply its prep verdict. |
| ChatNoteHostedService | !PD | report | Authority host |
| PipelineHealthService | always | report | Authority host |
| WorkspaceArtifactPushWorker | !PD | infra | Authority host |
| WorkspaceRepositoryMaintenanceWorker | !PD | infra | Authority host |
| WorkspaceEvidenceWorker | !PD | infra | Authority host |
| ReviewDecisionOrchestrator | loop | decide, switched | Remote coding completion, review report and gate result follow their profiles (`CodingCompletion`, `ReviewReport`, `GateResult`). The local legacy review loop (`ProcessDoneAsync`, `ProcessNoOpAsync`) retires with local execution (D16, I11). |
| AutoReviewPostProcessingWorker | loop | decide, switched | Same as ReviewDecisionOrchestrator: it decides through `ReviewDecisionOrchestrator.ProcessCardAsync`. |
| AutoReviewPostProcessingRecoveryService | loop | apply-recover | Authority |
| RemoteReviewEvidenceProjectionWorker | loop | apply-recover | Authority |
| RemoteReviewSettlementReconciler | loop | apply-recover | Authority |
| BatchGatePilotHostedService | loop | decide, switched | `GateResult` profile for every batch gate outcome, releases and red per-task gates (`HoldForEngineBatchDecision`, AGT-3234). Batch forming, closing and the advisor move with AGT-3055. |
| IntakeHostedService | !PD, per project | decide, operator | AI Operator decider `intake-routing` (`IntakeRoutingPolicy`): an already-done card goes to a person in Human Review. Effect: `IntakeRoutingEffect`. |
| GitInventoryRefreshHostedService | !PD | infra | Authority host |
| ProjectRepositoryVisibilitySync | always | infra | Authority host. Copies project visibility settings to registered Task Server repositories. |
| GitStateIndexService | !PD | infra | Authority host |
| GitBranchRetentionHostedService | !PD | infra | Authority host |
| CompletedPushWorker | !PD | infra | Authority host |
| CompletedPushBackstopHostedService | !PD | apply-recover | Authority |
| AcceptedIntegrationWorker | !PD | apply-recover | Authority |
| IntegrationPushWorker | !PD | infra | Authority host |
| InterruptedIntegrationGateRecoveryHostedService | !PD | apply-recover | Authority |
| AcceptedIntegrationBackstopHostedService | !PD | apply-recover | Authority |
| IntegrationPushBackstopHostedService | !PD | apply-recover | Authority |
| AcceptanceRailHostedService | !PD | decide, operator | AI Operator decider `acceptance-rail` (`AcceptanceRailDecisionPolicy`). Effect: `AcceptanceRailHostedService` accepts, requeues, replays, escalates or records a held bounce (A0). |
| GateEnvironmentRetryHostedService | !PD | decide, operator | AI Operator decider `gate-environment-retry` (`GateEnvironmentRetryPolicy`). Effect: `GateEnvironmentRetryService` replays or parks. The operator's Retry integration intent is validated, not decided. |
| OperatorSweepHostedService | !PD | decide, switched | `OperatorSweep` profile (`OperatorSweepPolicy`). Effect: `MonolithOrchestrationApplier` with `OperatorSweepActions`. |
| WatcherHostedService | !PD, off by default | report | Authority host (drafts proposals, no lane move) |
| EnvironmentHealthWorker | !PD, not Test | report | Authority host (IP-14 environment health checks; opens incident and certificate cards in Backlog and comments on them, no lane move) |
| OrphanReaperHostedService | !PD | infra | Authority host |
| CleanContextRetentionHostedService | !PD | infra | Authority host |
| StaleProgressSweepHostedService | !PD | decide, operator | AI Operator decider `stale-progress` (`StaleProgressPolicy`). Effect: `StaleProgressArchiver`. Folders without `task.json` are not cards; removing or archiving them stays authority filesystem recovery. |
| RunLivenessMonitorHostedService | !PD | decide, operator | AI Operator decider `run-liveness` (`RunLivenessPolicy`). Effect: `RunLivenessMonitor`. The boot adoption scan submits the same facts. |
| SteerTimeoutMonitorHostedService | !PD | decide, operator | AI Operator decider `steer-timeout` (`SteerTimeoutPolicy`). Effect: `SteerTimeoutMonitor`. |
| ParkedCardRecallSweepHostedService | always | report | Authority host |
| WikiCacheWarmupService | always | infra | Authority host |
| WikiPublicationSyncService | always | infra | Authority host |
| CliVersionMonitorHostedService | not Test | infra | Authority host |
| TaskWatcherService | always | infra | Authority host |
| TaskRunnerService | !PD | decide, local | Monolith: local execution (`RunOutcomePolicy`). Claimable work runs on runner hosts (D16 A); the local runner retires with I11. |
| SourceWatcher | always | infra | Authority host |
| CompanionSyncService | !PD | infra | Authority host (relays operator commands) |

## Decisions outside hosted services

| Handler | Class | Owner after AGT-3060 |
|---|---|---|
| `POST /api/runner/completion`: run ended without a terminal outcome | decide, operator | AI Operator decider `timeout-salvage` (`RunTimeoutSalvageContinuationPolicy`). Effect: `RunTimeoutSalvageEffect`. |
| `POST /api/runner/completion`: verified completion lane | decide, switched | `CodingCompletion` profile |
| `POST /api/runner/completion`: blocked, needs-input, provider rejection, claim-failure budget | decide, event | Residual in monolith (follow-up of AGT-3060; moves with the Task Server completion plane, I11). Not held: it applies the terminal outcome a runner host reports, and refusing it would lose the result. |
| `POST /api/runner/lease/release`: lost worker continuation | decide, event | Residual in monolith (follow-up of AGT-3060). The continuation is prepared before the lease is released, under the claim gate; it moves with the release plane (I11). Not held, for the same reason. |
| `POST /api/v1/reviews/attempts/{attemptId}/report` | decide, switched | `ReviewReport` profile |

## Verification

- `AiOperatorDecisionTests` covers every decider and autonomy class, the
  switch (the default decides in the monolith through the library, the
  `OperatorDecision` switch alone selects the engine), the per-decider
  occurrence store, and an end-to-end liveness case: with the engine stopped
  the card stays in `3-progress` with one pending occurrence; after the engine
  loop completes it, the card is in `2-ready`. The liveness receipt keeps a card
  that entered a visible waiting phase or gained remote lease history after its
  facts were submitted.
- `TaskBatchMoveTests` runs the delivery-chain reconciler on both switch
  positions: on `Monolith` it reopens and escalates in the same pass; on
  `Engine` it moves nothing while the engine is stopped and completes the same
  scenario once the engine decides.
- `OrchestrationEngineReadinessTests` covers the readiness window, the profiles
  that require it, the probe and the outage alert.
- `MonolithDecisionBoundaryTests` (Category=Guard) fails when a host forces a
  decision mode, when a registered hosted service is missing from this
  inventory, when a `decide` row is not `decide, operator` or
  `decide, switched` (or a new `decide, local`), when such a service does not
  reach `AiOperatorDecisionGateway` or `MonolithTransitionSwitch`, when an AI
  Operator decider calls a library rule around the switch, or when a new
  host-defined policy, rules or breaker class appears in a workflow feature
  folder.
