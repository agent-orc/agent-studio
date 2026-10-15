# Monolith hosted services and decision ownership

AGT-3060 (I18) implements AGT-W65 D14 A: the Orchestrator Engine is the only
decider. This inventory lists every hosted service and background handler
that `backend/Host/Program.cs` and `OrchestrationLoopRegistration` register,
classifies it, and names the owner of its decision. Update the table whenever a
hosted service is added, moved or removed. The guard
`backend.Tests/Architecture/MonolithDecisionBoundaryTests.cs` freezes the
remaining monolith decision code in
`backend.Tests/Architecture/Fixtures/monolith-decision-baseline.txt`. That list
only shrinks.

## Classes

| Class | Meaning |
|---|---|
| decide | The service chooses a lane transition, fix round, continuation, requeue, demotion, escalation or batch outcome for a card. It moves to the engine. |
| apply-recover | The service applies an already recorded decision, replays a keyed write, or recovers authority state after a restart. It stays with the task authority until the Task Server cutover (I11). |
| report | Diagnostics, alarms, advisories or projections; no lane move. |
| infra | Caches, watchers, Git maintenance, process reaping, retention, sync. |

## Decision authority after AGT-3060

- A production host (every environment except `Test` and the in-process test
  host) calls `OrchestrationDecisionAuthority.RequireEngine`. It pins every
  transition profile (`CodingCompletion`, `ReviewReport`, `GateResult`,
  `OperatorSweep`) and every AI Operator decider (`OperatorDecision`) to
  `Engine`. A configured `Monolith` or `Shadow` transition fails startup.
  The Monolith and Shadow decision modes, and the rollout's rollback to the
  monolith policy, remain for test hosts only. The loop-host switch
  `Orchestration:ExecutionMode` no longer selects who decides.
- The moved background deciders submit typed facts through
  `AiOperatorDecisionGateway` as `OperatorDecision` occurrences (AGT-W73 O1 A:
  the AI Operator role, periodic or event-triggered). The engine's
  `AiOperatorDecisionLoop` decides with `AiOperatorDecisionPolicy` and returns a
  receipt with the decider, effect, autonomy class and reason. The AI Operator
  acts on A0 and A1, records an A2 action as a proposal for a person, and asks a
  person instead of taking an A3 action. The authority applies an Act or Ask
  receipt through the decider's `IAiOperatorEffect`. Each effect checks the lane,
  the frozen subject and any heartbeat again before it writes.
- With the engine stopped, these deciders only record pending occurrences;
  `MonolithOrchestrationRecoveryService` raises the existing engine-unavailable
  alert after one minute.

## Inventory

Registration abbreviations: `!PD` means not the PublicDemo execution profile;
`loop` means `OrchestrationLoopRegistration` under the loop-host switch
`Orchestration:ExecutionMode=Monolith` and `!PD`.

| Hosted service | Registration | Class | Owner after AGT-3060 |
|---|---|---|---|
| MonolithOrchestrationRecoveryService | always | apply-recover | Authority. Activates prepared occurrences, replays `applying` receipts, alerts on engine outages. Its rollback decision path runs only on a test host. |
| LinkSupervisor | always | infra | Authority host |
| OrchestratorChatLegacyMigrationHostedService | !PD | infra | Authority host |
| DecisionReminderSweepHostedService | always | report | Authority host |
| DeliveryChainReconciler | always | decide | Residual in monolith (follow-up of AGT-3060): `DeliveryLanePolicy`, `GateFailureRouter` |
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
| CauseBreakerHostedService | !PD | decide | Residual in monolith (follow-up of AGT-3060): `CauseBreakerPolicy.Close` releases waiting cards |
| SystemLoadThrottle | always | infra | Authority host |
| AutoTagCreationWorker | not Test | infra | Authority host |
| AutoTagBackfillQueue | not Test | infra | Authority host |
| TagMaintenanceWorker | !PD | infra | Authority host |
| HardHealthCheckHostedService | !PD | report | Authority host |
| SoftReasoningHostedService | !PD, off by default | report | Authority host |
| AutoInterventionHostedService | !PD, off by default | decide | Residual in monolith (follow-up of AGT-3060; AI Operator candidate) |
| MetaCycleHostedService | !PD, off by default | decide | Residual in monolith (follow-up of AGT-3060; AI Operator candidate) |
| OrchestratorPrepHostedService | !PD, off by default | decide | Residual in monolith (follow-up of AGT-3060): `OrchestratorPrepRules` |
| ChatNoteHostedService | !PD | report | Authority host |
| PipelineHealthService | always | report | Authority host |
| WorkspaceArtifactPushWorker | !PD | infra | Authority host |
| WorkspaceRepositoryMaintenanceWorker | !PD | infra | Authority host |
| WorkspaceEvidenceWorker | !PD | infra | Authority host |
| ReviewDecisionOrchestrator | loop | decide | Engine for remote coding completion, review report and gate result (profiles pinned to Engine). The local legacy review loop (`ProcessDoneAsync`, `ProcessNoOpAsync`) is residual and retires with local execution (D16, I11). |
| AutoReviewPostProcessingWorker | loop | decide (delegates) | Same as ReviewDecisionOrchestrator |
| AutoReviewPostProcessingRecoveryService | loop | apply-recover | Authority |
| RemoteReviewEvidenceProjectionWorker | loop | apply-recover | Authority |
| RemoteReviewSettlementReconciler | loop | apply-recover | Authority |
| BatchGatePilotHostedService | loop | decide | Engine for the batch gate result (`GateResult` profile, `HoldForEngineBatchDecision`). Batch forming, closing and the advisor move with AGT-3055. |
| IntakeHostedService | !PD, per project | decide | Residual in monolith (follow-up of AGT-3060) |
| GitInventoryRefreshHostedService | !PD | infra | Authority host |
| GitStateIndexService | !PD | infra | Authority host |
| GitBranchRetentionHostedService | !PD | infra | Authority host |
| CompletedPushWorker | !PD | infra | Authority host |
| CompletedPushBackstopHostedService | !PD | apply-recover | Authority |
| AcceptedIntegrationWorker | !PD | apply-recover | Authority |
| IntegrationPushWorker | !PD | infra | Authority host |
| InterruptedIntegrationGateRecoveryHostedService | !PD | apply-recover | Authority |
| AcceptedIntegrationBackstopHostedService | !PD | apply-recover | Authority |
| IntegrationPushBackstopHostedService | !PD | apply-recover | Authority |
| AcceptanceRailHostedService | !PD | decide | Residual in monolith (follow-up of AGT-3060): `AcceptanceRailPolicy` |
| GateEnvironmentRetryHostedService | !PD | decide, moved | Engine: AI Operator decider `gate-environment-retry` (`GateEnvironmentRetryPolicy`). Effect: `GateEnvironmentRetryService` replays or parks. The operator's Retry integration intent is validated, not decided. |
| OperatorSweepHostedService | !PD | decide, moved | Engine: `OperatorSweep` stage (`OperatorSweepPolicy`), pinned to Engine. Effect: `MonolithOrchestrationApplier` with `OperatorSweepActions`. |
| WatcherHostedService | !PD, off by default | report | Authority host (drafts proposals, no lane move) |
| OrphanReaperHostedService | !PD | infra | Authority host |
| CleanContextRetentionHostedService | !PD | infra | Authority host |
| StaleProgressSweepHostedService | !PD | decide, moved | Engine: AI Operator decider `stale-progress` (`StaleProgressPolicy`). Effect: `StaleProgressArchiver`. Folders without `task.json` are not cards; removing or archiving them stays authority filesystem recovery. |
| RunLivenessMonitorHostedService | !PD | decide, moved | Engine: AI Operator decider `run-liveness` (`RunLivenessPolicy`). Effect: `RunLivenessMonitor`. The boot adoption scan submits the same facts. |
| SteerTimeoutMonitorHostedService | !PD | decide, moved | Engine: AI Operator decider `steer-timeout` (`SteerTimeoutPolicy`). Effect: `SteerTimeoutMonitor`. |
| ParkedCardRecallSweepHostedService | always | report | Authority host |
| WikiCacheWarmupService | always | infra | Authority host |
| WikiPublicationSyncService | always | infra | Authority host |
| CliVersionMonitorHostedService | not Test | infra | Authority host |
| TaskWatcherService | always | infra | Authority host |
| TaskRunnerService | !PD | decide | Residual in monolith: local execution (`RunOutcomePolicy`). Claimable work runs on runner hosts (D16 A); the local runner retires with I11. |
| SourceWatcher | always | infra | Authority host |
| CompanionSyncService | !PD | infra | Authority host (relays operator commands) |

## Decisions outside hosted services

| Handler | Class | Owner after AGT-3060 |
|---|---|---|
| `POST /api/runner/completion`: run ended without a terminal outcome | decide, moved | Engine: AI Operator decider `timeout-salvage` (`RunTimeoutSalvageContinuationPolicy`). Effect: `RunTimeoutSalvageEffect`. |
| `POST /api/runner/completion`: verified completion lane | decide, moved | Engine: `CodingCompletion` profile, pinned to Engine |
| `POST /api/runner/completion`: blocked, needs-input, provider rejection, claim-failure budget | decide | Residual in monolith (follow-up of AGT-3060; moves with the Task Server completion plane, I11) |
| `POST /api/runner/lease/release`: lost worker continuation | decide | Residual in monolith (follow-up of AGT-3060). The continuation is prepared before the lease is released, under the claim gate; it moves with the release plane (I11). |
| `POST /api/v1/reviews/attempts/{attemptId}/report` | decide, moved | Engine: `ReviewReport` profile, pinned to Engine |

## Verification

- `AiOperatorDecisionTests` covers every decider and autonomy class, the
  production authority, the per-decider occurrence store, and an end-to-end
  liveness case: with the engine stopped the card stays in `3-progress` with
  one pending occurrence; after the engine loop completes it, the card is in
  `2-ready`.
- `MonolithDecisionBoundaryTests` (Category=Guard) fails on a reintroduced
  decision in `backend/`: a new call into a decision-library policy or the
  test-host adapter, a grown call site, a decision call in a moved decider, or a
  new host-defined policy, rules or breaker class in a workflow feature folder.
