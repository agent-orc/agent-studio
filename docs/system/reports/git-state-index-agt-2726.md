# Background Git State Index, AGT-2726

Git-derived board state moved off the request path and into one background index
per repository. This note records the measurement that motivated it, the design,
and the bound that keeps it from regressing.

## Measurement before the change

Operator laptop, 6 September 2026, 20:00 to 20:55, from `git-info request=` log
lines. The laptop was also running Studio, the Task Server, and the review gates.

| request | calls | git spawns | git time | wait time (wall minus git) |
|---|---:|---:|---:|---:|
| tasks/list-refresh | 315 | 2,243 | 1,085 s | 138 s |
| git/inventory | 173 | 417 | 872 s | 7 s |
| board/integration-status | 437 | 396 | 54 s | 92 s |
| board/merge-status | 328 | 261 | 49 s | 0 s |
| wiki/cache-fill | 70 | 611 | 159 s | 0 s |
| tasks/grouped | 351 | 0 | 0 s | 2,775 s |
| tasks/list | 27 | 0 | 0 s | 858 s |

About 3,980 git processes in 55 minutes, roughly 72 per minute. Worst single
requests at 20:50: `tasks/grouped` 95.8 s wall with zero spawns, `tasks/list-refresh`
109 s with 18 spawns, `git/inventory` 23 s, `publish/derive` 35 s.

The board endpoint owned no git work yet waited the longest. Two causes:

1. The task-list projection refreshed on a two-second interval whenever the board
   was polled, whether or not any ref had moved. That accounted for most of the
   spawns.
2. The refresh ran on the thread pool and blocked there: `Parallel.ForEach` over
   repositories, a process-wide four-slot semaphore acquired with a blocking
   `Wait()`, and single-flight caches whose readers block. `tasks/grouped` is a
   synchronous handler, so it queued behind work it had no part in.

The perf wave of 1 September (AGT-2699 to AGT-2702, archived) had already removed
no-op invalidations and per-scan costs. What was left was structural.

## Design

One `GitStateIndex` per backend, driven by `GitStateIndexHostedService`.

- **State.** Per repository: HEAD, branch tips, worktree list, and each project's
  inventory. Captured through `GitService.ComputeProjectInventoryUncached`, so a
  capture is one bounded set of git reads per project rather than a separate fork
  per fact.
- **Triggers.** Recursive `FileSystemWatcher` on the repository's shared git
  metadata directory, which covers `HEAD`, `refs/`, `packed-refs`, `reftable/`
  and every linked worktree's `worktrees/<name>/HEAD`; plus the Task Server's own
  move and bulk-change events; plus a five-minute safety sweep. Path
  classification is pure (`GitIndexPathClassifier`) and rejects lock files and
  churn such as `index`, `logs/` and `COMMIT_EDITMSG`.
- **Coalescing.** 400 ms debounce, bounded by a two-second maximum wait. Single
  flight per repository: a repository already capturing is not started again and
  its pending triggers fold into the next run.
- **Spawn budget.** Two repositories at a time, each on a dedicated long-running
  thread so the thread pool stays free for Kestrel. Git spawns started inside a
  capture run at below-normal priority on Windows. Every run logs
  `git-index-run repository=… spawns=… ms=… trigger=…`, and a run over five
  seconds also logs its slowest subcommand.
- **Reads.** Request paths call `Snapshot` or `InventoryFor` and return the last
  capture immediately. The former process-wide read-only gate is now a
  per-repository lock plus the same two-repository budget, and it is only ever
  acquired from background work.
- **Freshness stamp.** `X-Git-State-At` and `X-Git-State-Stale` on every affected
  response, and only as headers. `GET /api/tasks` returns an array and
  `GET /api/git/inventory` a positional record, so neither can carry a scalar
  without a wrapper. `GET /api/tasks/grouped` looks like it could, and an early
  build did add the two values as fields there; driving the real app showed the
  Explorer's project rows throwing `lane is not iterable`, because clients read
  that object as a lane map and iterate every value as a task array. One
  mechanism for all four endpoints, read once by an HTTP interceptor.
- **Propagation.** A capture that changed state advances the index generation,
  which invalidates the task-list projection, and pushes the existing coarse
  `jobsChanged` SignalR event so the board re-pulls without polling.
- **Telemetry.** `GitProcessTelemetry` keeps a one-hour rolling window of request
  scopes and of every git spawn, and answers per-label p50/p95 plus spawns per
  minute. `GET /api/admin/performance/git-state` serves that together with each
  repository's index age and the last runs. There is deliberately no second
  counter.

Also changed: the task-list projection's safety interval went from two seconds to
fifteen, because a ref move now invalidates it through the index generation
rather than through the clock, and its refresh runs on a dedicated thread.

`GET /api/git/history` still reads on demand. The page an operator asks for by
offset is not part of the captured state, so it cannot be served from the index.

## Verification

`dotnet test backend.Tests/OrchestratorApi.Tests.csproj --filter Category!=MachineBound`
covers the pure policy matrices and the index behaviour in
`backend.Tests/GitStateIndexTests.cs`: trigger classification, the run policy
truth table, sweep due-ness, the SLO thresholds, percentile edge cases, debounce,
single-flight coalescing, stale-while-revalidate reads during a run, the
cross-repository budget, generation stability across an unchanged capture, and
capture failure leaving the previous snapshot in place.

`backend.Tests/GitStateIndexLoadReplayTests.cs` (`Category=MachineBound`) replays
the measured request mix against a real backend and a real repository: 351
`tasks/grouped`, 27 `tasks/list`, and 173 `git/inventory` calls. Observed on the
development host:

| quantity | before | after |
|---|---:|---:|
| git spawns for the replayed request mix | about 3,980 | 19 |
| `tasks/grouped` request-path spawns | 0 | 0 |
| `git/inventory` request-path spawns | 417 | 0 |
| `tasks/grouped` p95 | 95.8 s worst case | under 1 ms |

The test also asserts that a ref move reaches the index inside its ten-second
freshness budget and that both stamp headers reach the wire.

Frontend: `frontend/src/app/services/git-state-stamp.interceptor.spec.ts` covers
the header-to-signal path, and `frontend/e2e/board/git-state-stamp.spec.ts`
covers the status-bar reading and the push refresh.
