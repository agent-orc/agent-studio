# Git State Index, AGT-2726

Date: 2026-09-07
Status: Implemented. Extends the request-path caching from
[git-info-performance-agt-2007.md](git-info-performance-agt-2007.md) with a
background index that decouples recompute cadence from request cadence.

## Problem

The 2026-09-06 20:00-20:55 measurement window showed the board endpoints
themselves spawn no Git (`board/integration-status`, `board/merge-status`)
but wait behind `tasks/list-refresh`, which ran roughly six times a minute and
spawned about seven Git processes per call - 2,243 spawns across 315 calls in
55 minutes, ~3,980 total git-related spawns in the window, ~72/minute. The
worst single `tasks/grouped` request took 95.8 s with zero spawns of its own,
blocked entirely on a shared refresh.

Root cause was structural, not a missing cache: `TaskListGitProjectionCache`
recomputed the whole board's Git-derived projection (merge, integration,
publish, test-run signals, across every project) whenever a request landed
more than a fixed TTL after the last refresh. Under continuous board traffic
that TTL is effectively always expired, so the trigger frequency tracked
request frequency, not actual repository change frequency.

## Design

One background index per repository, `GitStateIndexService`
(`backend/Features/Git/GitStateIndexService.cs`):

- **Change-driven.** A `FileSystemWatcher` on each repository's `.git`
  directory (`HEAD`, `refs/`, `packed-refs`, worktree `HEAD` files) plus
  `TaskWatcherService.OnJobChanged` (task-folder writes - an accepted
  integration or delivery is itself a change signal) trigger a debounced
  (default 400 ms) refresh. A periodic sweep (default 45 s) re-checks a cheap
  `GitRefSignature` per repository as a safety net for anything the watcher
  missed (network filesystems, git operations that bypass the normal
  ref-update path).
- **Single-flight, coalesced.** A trigger arriving while a repository's run is
  in flight sets one pending rerun instead of starting a second run; a burst
  of N triggers inside one debounce window or one in-flight run produces at
  most 2 actual runs, not N.
- **Bounded cross-repository concurrency.** `GitStateIndex:MaxConcurrentRepos`
  (default 2) caps how many repositories index at once process-wide, so a
  workspace with many repositories cannot fan out unbounded git processes on
  one trigger burst (a sweep tick, a bulk operator action).
- **Reuses existing per-family caching**, it does not replace it:
  `BoardMergeStatusService`/`TaskIntegrationStatusService`/
  `TaskPublishableService`/`TestRunService` keep their own internal
  ref-fingerprint-gated caches; the indexer only changes what decides *when*
  to call them. `GitService.GetProjectInventory`'s existing signature-gated
  cache/channel is reused unchanged - the indexer just calls it proactively on
  every change-driven trigger instead of waiting for a request to notice
  staleness.
- **Request paths only read.** `TaskListGitProjectionCache` is a pure
  per-repository snapshot store (`SetSnapshot`/`MarkRefreshing` are the
  indexer's write API); `ReadCacheOnly` merges the last completed snapshot for
  every repository the requested tasks touch and starts no Git work. See
  [domains/tasks.md#board-state-source-agt-2726](../domains/tasks.md#board-state-source-agt-2726)
  for the wire contract (`gitStateAt`/`stale`) and the SignalR
  `gitStateChanged` push.

## Telemetry

`GitProcessTelemetry` (unchanged accounting primitive - no second counter)
gained a bounded rolling window per label so an Admin surface can read p50/p95
and a spawns/minute rate without re-deriving them from raw logs. Each index
run additionally logs a dedicated line for operational diagnosis:

```
git-index-run repository=<name> spawns=<n> ms=<elapsed> trigger=<fs-watch|task-event|sweep|startup|manual>
```

A run slower than `GitStateIndex:SlowRunWarnMs` (default 5 s) also logs the
slowest subcommand. `GET /api/admin/git-telemetry` surfaces per-endpoint
p50/p95/spawn-rate plus per-repository index age, warning when
`tasks/grouped` p95 exceeds 1 s or total spawns exceed 20/minute.

## Verification

- `GitStateIndexServiceTests` (`backend.Tests/GitStateIndexServiceTests.cs`):
  single-flight coalescing (a 300-trigger burst inside one debounce window
  produces at most 2 runs), a trigger arriving mid-run coalesces into exactly
  one rerun, a stale snapshot keeps serving reads during an in-flight run, the
  request path records zero spawns even after a run that did spawn, bounded
  cross-repository concurrency, and change detection via a plain filesystem
  write under `.git/refs` (no git binary required for that test).
- `ReplayOfMeasuredTriggerPattern_RunCountStaysBoundedByDebounceNotByTriggerVolume`
  replays the measured call volume from the 2026-09-06 window (1,253 combined
  trigger-shaped calls) against one repository and asserts the run count stays
  a small constant instead of scaling with trigger volume - the structural
  property that keeps real spawns/minute under budget regardless of how busy
  the board gets.
- `GitProcessTelemetryTests` covers the new rolling-stats percentile/rate
  computation and the slowest-command diagnostic hook.
- `JobsEndpointPerfTests.TaskListEndpoints_ColdAndHeadChurn_StartNoGitProcessAndReturnUnderOneSecond`
  is the end-to-end proof: a real `WebApplicationFactory` host, a real git
  commit, the indexer's own `FileSystemWatcher` picking it up, and
  `/api/tasks` still spawning zero Git processes before and after.
- `TaskListGitProjectionCacheTests` pins the snapshot store's read/merge/
  freshness contract now that it no longer self-triggers.
