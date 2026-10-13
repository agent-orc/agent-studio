# Integration lane measurement, AGT-3070

The Task Server appends typed `integration-lane-events.jsonl` records to each delivery folder. The 24-hour lane projection and the 7-day or 30-day gate projection share `GET /api/gate-economics`. The endpoint reads per-delivery gate receipt headers and durable batch execution facts for gate time, wait and rung; it reads typed lane events for arrival, lock, gate and publication times. Per-delivery queue wait is machine gate wait plus merge-lock wait. One batch gate `durationMs` is divided by its manifest member count to express minutes per merge. A missing historical field is unknown.

| Model input | Measured definition | First post-instrumentation week |
|---|---|---|
| g | Median and p90 `durationMs` of the batch-flow gate receipts | Pending 12 to 19 October 2026 |
| f | Red gate-finished events divided by terminal gate-finished events | Pending a complete delivery wave |
| p | Delivery dequeue to gate request, less merge-lock wait | Pending a complete delivery wave |
| lambda | Enqueued deliveries per hour by project | Pending a complete delivery wave |

The historical 28 to 29 September Windows receipts are a comparison, not batch-flow input. All eight were `Fail` with `failureKind=Code` and `budget=none`. Seven stopped in `backend.Tests` with three omitted test commands; one stopped in `runner.Tests` with two omitted commands. The median historical gate duration was 3,073,130 ms (51.2 minutes); p90 was 3,234,838 ms (53.9 minutes). Each receipt below is `pre-develop-build-gate-N.log` in the card's `post-steps` folder on the Windows Task Server, read through `GET /api/tasks/{card}/files/post-steps/...` on 13 October 2026. Every test-selection audit is `Level=full`, `Selector=deterministic-map-full-fallback`, 4 selected commands and `FullSuiteRequired=true`, `FullSuiteRan=false`.

| Merge | Card | Receipt | Verdict | durationMs | Budget | Attempted / omitted | Red step and failing tests |
|---|---|---|---|---|---|---|---|
| `0e2b0f8d` | AGT-2940 | gate-1 | Fail (Code) | 3,016,313 | none | 1 / 3 | `backend.Tests`: PromptEnrichmentServiceTests.Prepare_AgentStudioRepository_PreservesCatalogueBlocks |
| `d8f11a5f` | AGT-2887 | gate-1 | Fail (Code) | 3,001,541 | none | 1 / 3 | `backend.Tests`: AutoPushStrategyTests.CompletedPushWorker_PushesQueuedCommitToMain, PromptEnrichmentServiceTests.Prepare_AgentStudioRepository_PreservesCatalogueBlocks, TaskIndexCacheTests.LaterMutation_DoesNotMoveGoalpostForAlreadyWaitingReader |
| `aebb45b6` | AGT-2920 | gate-1 | Fail (Code) | 3,012,606 | none | 1 / 3 | `backend.Tests`: PromptEnrichmentServiceTests.Prepare_AgentStudioRepository_PreservesCatalogueBlocks |
| `65b268d5` | AGT-2912 | gate-3 | Fail (Code) | 3,186,026 | none | 1 / 3 | `backend.Tests`: PromptEnrichmentServiceTests.Prepare_AgentStudioRepository_PreservesCatalogueBlocks |
| `9154dbd1` | AGT-2907 | gate-2 | Fail (Code) | 3,348,733 | none | 1 / 3 | `backend.Tests`: PromptEnrichmentServiceTests.Prepare_AgentStudioRepository_PreservesCatalogueBlocks |
| `c6673af8` | AGT-2927 | gate-4 | Fail (Code) | 3,055,919 | none | 1 / 3 | `backend.Tests`: PromptEnrichmentServiceTests.Prepare_AgentStudioRepository_PreservesCatalogueBlocks, TaskIndexCacheTests.MutationDuringInFlightRefresh_WaitsAndDrivesOneFreshFollowup |
| `ebbaef7d` | AGT-2738 | gate-1 | Fail (Code) | 3,090,341 | none | 1 / 3 | `backend.Tests`: PromptEnrichmentServiceTests.Prepare_AgentStudioRepository_PreservesCatalogueBlocks |
| `2272ce8c` | AGT-2915 | gate-4 | Fail (Code) | 3,143,131 | none | 2 / 2 | `runner.Tests`: DurableLeaseAuthorityTests.Granted_renewal_delivers_the_fenced_stop_without_losing_authority |

Q0 answer: `full-suite=not-run; omitted=3` came from a red backend test step, not from an out-of-budget step. The gate stops at the first red command and records the remaining selected commands as omitted. `PromptEnrichmentServiceTests.Prepare_AgentStudioRepository_PreservesCatalogueBlocks` was red in seven of the eight receipts. AGT-2912 `pre-develop-build-gate-4.log` gated `ebbaef7d` again with the same selection audit digest about one hour after AGT-2738, which is one observed same-SHA retest of 3,346,669 ms.

As of 12 October, the live Agent Studio batch-gate report had 0 closed batches, 0 eligible members and 0 full-suite runs; its green rate and latency were null. No full week of events from this instrumentation exists. Whether an agent round coincided with a stalled project queue, and what held that queue, are therefore unmeasured. AGT-W70 slices 2 to 5 and AGT-W69 slices 2 to 4 remain unpromoted until one full wave and the first complete week are observed. This is the working no-go for additional lane or gate behavior changes.
