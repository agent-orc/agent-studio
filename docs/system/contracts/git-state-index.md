# Git state index contract

Git-derived board state is a **background index**, not request-path work. This
is the contract every board endpoint, every git projection, and every future
cache layer must respect.

## The invariant

> No request path spawns a git process, and no request path waits on the index.

A request answers from the last completed snapshot and says how old it is. It
never blocks to make that snapshot newer.

## Why (AGT-2726)

Measured 2026-09-06, 20:00-20:55, from the `git-info request=` log lines on the
operator laptop that also runs Studio, the Task Server, and the review gates:

| request | calls | git spawns | git time | wait time (wall minus git) |
|---|---:|---:|---:|---:|
| tasks/list-refresh | 315 | 2,243 | 1,085 s | 138 s |
| git/inventory | 173 | 417 | 872 s | 7 s |
| board/integration-status | 437 | 396 | 54 s | 92 s |
| board/merge-status | 328 | 261 | 49 s | 0 s |
| wiki/cache-fill | 70 | 611 | 159 s | 0 s |
| tasks/grouped | 351 | 0 | 0 s | 2,775 s |
| tasks/list | 27 | 0 | 0 s | 858 s |

About 3,980 git processes in 55 minutes, roughly 72 per minute. The reading:
`tasks/grouped` **owns no git work at all** - zero spawns, 2,775 s of wait - and
was queued behind background refreshes that had parked thread-pool threads on
blocking semaphore waits and synchronous `Process.WaitForExit` calls. The worst
single `tasks/grouped` request took 95.8 s with zero spawns of its own.

The perf wave of 2026-09-01 (AGT-2699 to AGT-2702) removed no-op invalidations
and per-scan cost. What remained was structural: git-derived state was computed
on the request path under a shared gate, and every refresh trigger repeated it.

## The shape

```
ref watcher ─┐
task events ─┼─> GitStateIndex (schedule + stamps) ─> GitBackgroundExecutor ─> git
sweep       ─┘                    ▲                     (bounded, dedicated threads)
                                  │ read (dictionary lookup, never blocks)
                        tasks/grouped, tasks/list, git/inventory
```

| Piece | File | Owns |
|---|---|---|
| `GitIndexAdmissionPolicy` | `backend/Features/Git/StateIndex/GitIndexAdmissionPolicy.cs` | The pure Start / Coalesce / Wait / Idle decision. |
| `GitStateIndex` | `backend/Features/Git/StateIndex/GitStateIndex.cs` | Per-repository schedule, single-flight claim, and the `gitStateAt` / `stale` stamps. |
| `GitRepositoryRefWatcher` | `backend/Features/Git/StateIndex/GitRepositoryRefWatcher.cs` | `HEAD`, `packed-refs`, `refs/`, linked-worktree HEADs, `reftable/`. |
| `GitBackgroundExecutor` | `backend/Features/Git/StateIndex/GitBackgroundExecutor.cs` | The only threads allowed to spawn git for the index. |
| `GitRepositoryStateRefresher` | `backend/Features/Git/StateIndex/GitRepositoryStateRefresher.cs` | What one run computes. |
| `GitStateIndexHostedService` | `backend/Features/Git/StateIndex/GitStateIndexHostedService.cs` | Discovery, watcher wiring, sweep, dispatch, run telemetry. |

## Rules

1. **One index per repository.** A run computes that repository's integration
   and release ancestor sets, its publish derivation, and its branch / worktree
   / history inventory - into the same ref-fingerprinted caches the request
   paths already read.
2. **Change-driven, with a slow sweep as a safety net.** Triggers are ref-file
   changes, the lane moves and bulk changes the Task Server itself produces,
   and a periodic sweep (default 2 min) for what no watcher reports. A trigger
   is debounced (default 400 ms) because git rewrites refs in bursts.
3. **Single-flight per repository.** A run in progress is never duplicated.
   Concurrent triggers coalesce; the dirty bit is cleared at *claim* time, so a
   change arriving mid-run schedules the next one instead of being swallowed.
4. **Bounded across repositories.** `MaxConcurrentRepositories` (default 2) is
   the background process budget for index runs. The executor is sized one
   thread wider so the board's per-card assembly (`tasks/list-refresh`) is never
   starved behind index runs in a many-repository workspace. It uses dedicated,
   below-normal-priority threads - never the thread pool - because thread-pool
   starvation, not git latency, was the measured defect. On Windows the child
   process is also dropped to `BelowNormal`.
5. **Stale-while-revalidate on every read.** `tasks/grouped` carries
   `gitStateAt` and `gitStateStale` in its body; `GET /api/tasks` (a bare array
   on the wire) carries `X-Git-State-At` and `X-Git-State-Stale` headers;
   `git/inventory` carries `computedAt` and `stale`. The board renders the
   stamp quietly and updates through the existing SignalR push.
6. **One counter.** `GitProcessTelemetry` is the only source. Its rolling
   one-hour window feeds `GET /api/admin/git-performance`; do not add a second
   counter beside it.

## Telemetry

| Line | Meaning |
|---|---|
| `git-index-run repository=… spawns=… ms=… trigger=…` | One completed index run. |
| `git-index-run-slow repository=… ms=… slowest=… slowestMs=…` | A run over `SlowRunThreshold` (default 5 s), with the command that made it slow. |
| `git-info request=git/index-run …` | The run's spawn rollup, same format as every other scope. |

`GET /api/admin/git-performance` returns p50/p95 per endpoint over the last
hour, index age per repository, spawns per minute, and warnings when
`tasks/grouped` p95 exceeds the budget (default 1 s), when spawns exceed the
budget (default 20/min), or when a request-path label reports a non-zero spawn
count at all.

## Configuration

`GitStateIndex` section, all optional (`backend/Features/Git/StateIndex/GitStateIndexOptions.cs`):
`Debounce`, `SweepInterval`, `TickInterval`, `MaxConcurrentRepositories`,
`SlowRunThreshold`, `LowProcessPriority`, `GroupedP95Budget`,
`SpawnsPerMinuteBudget`. Values are clamped to sane ranges on read.

## Budget

- `tasks/grouped` p95 under 300 ms, with zero git spawns on the request path.
- Under 20 git spawns per minute at the same freshness as before: index age
  under 10 s after a ref change.

## Not in scope here

ETag and payload trim for the board endpoints is AGT-2703, a follow-up that
builds on this index. Do not fold it in.

## Tests

`backend.Tests/GitStateIndexTests.cs` (admission matrix, invalidation,
coalescing, stale-while-revalidate, bounded concurrency, and the replayed
55-minute trigger pattern as a spawn regression bound),
`backend.Tests/GitPerformanceWindowTests.cs` (percentiles, retention, SLO
warnings), `backend.Tests/TasksEndpointPerfTests.cs` (zero spawns and the stamp
on the live endpoints), `frontend/e2e/board/git-state-stamp.spec.ts`.

## History

- AGT-2007 - the measurement half: `GitProcessTelemetry` and the first git-info
  caches ([reports/git-info-performance-agt-2007.md](../reports/git-info-performance-agt-2007.md)).
- AGT-2699 to AGT-2702 - no-op invalidation and per-scan cost removal.
- AGT-2726 - this contract.
