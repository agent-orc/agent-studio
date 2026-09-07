---
id: slow-board-git-state-on-request-path
title: "Board is slow while spawning no git of its own, because git-derived state is computed on the request path"
status: fixed
first-seen: 2026-09-06T20:00:00Z
last-seen: 2026-09-06T20:55:00Z
severity: major
category: performance
tags: [board, git, performance, thread-pool, telemetry, stale-while-revalidate, git-state-index]
affects:
  - backend/Features/Git/StateIndex/GitStateIndex.cs
  - backend/Features/Git/StateIndex/GitBackgroundExecutor.cs
  - backend/Features/Git/StateIndex/GitStateIndexHostedService.cs
  - backend/Features/Tasks/TaskListGitProjectionCache.cs
  - backend/Features/Tasks/TaskCrudEndpoints.cs
related-tasks: [AGT-2007, AGT-2699, AGT-2700, AGT-2701, AGT-2702, AGT-2726]
related-adrs: []
---

# slow-board-git-state-on-request-path

**Symptom.** The board takes seconds - sometimes a minute and a half - to load,
while nothing in the logs blames it. `GET /api/tasks/grouped` and
`GET /api/tasks` look innocent: their `git-info request=` rollups report
`spawns=0`. Other requests in the same window report large spawn counts and
large `gitMs`. Restarting the backend helps for a few minutes.

**How to confirm.** Aggregate the `git-info request=` lines over an hour and
compare `wallMs` against `gitMs` per label. The signature is a label with
**zero spawns and enormous wall time**:

| request | calls | git spawns | git time | wait time (wall minus git) |
|---|---|---:|---:|---:|
| tasks/grouped | 351 | 0 | 0 s | 2,775 s |
| tasks/list | 27 | 0 | 0 s | 858 s |
| tasks/list-refresh | 315 | 2,243 | 1,085 s | 138 s |

That is the 2026-09-06 20:00-20:55 measurement: about 3,980 git processes in
55 minutes, roughly 72 per minute, on the operator laptop that also runs
Studio, the Task Server, and the review gates. Since AGT-2726 the same view is
available live at `GET /api/admin/git-performance` (p50/p95 per endpoint over
the last hour, index age per repository, spawns per minute).

**Why it is not a git problem.** The slow endpoint owns no git work at all. It
was queued behind *background* refreshes that ran on the thread pool: each
refresh fanned four lookups out with `Task.Run`, each lookup blocked on a
process-wide semaphore and then on a synchronous `Process.WaitForExit`. With
refreshes starting about six times a minute and running for seconds, and the
pool growing only about one thread per 500 ms past its minimum, ASP.NET request
threads ended up waiting behind git work they had nothing to do with. Adding
more caching does not fix this; the cost is structural, because git-derived
state is derived *on demand* and every refresh trigger repeats it.

**Fix (AGT-2726).** Git-derived board state became a background index. See
[docs/system/contracts/git-state-index.md](../../../system/contracts/git-state-index.md)
for the contract. In short:

1. One index per repository, owned by `GitStateIndexHostedService`, triggered
   by a ref watcher, by the Task Server's own lane and bulk events, and by a
   slow safety sweep. Single-flight per repository; concurrent triggers
   coalesce.
2. `GitBackgroundExecutor` runs it on a small pool of dedicated,
   below-normal-priority threads. Nothing background touches the thread pool.
3. Request paths read the last snapshot and return immediately, with a
   `gitStateAt` stamp and a `stale` flag.

**If you see it again.** Check, in this order:

1. `GET /api/admin/git-performance` - is `tasks/grouped` p95 over budget, and
   does any request-path label report a non-zero `spawns`? A request path with
   spawns is the defect itself, not a symptom.
2. `git-index-run-slow repository=… slowest=…` lines - one repository whose run
   exceeds five seconds, named together with the command that made it slow.
3. Index age per repository. An age that keeps climbing while `running` is
   false means triggers are not arriving: the ref watcher failed to attach
   (logged as `git-index watcher could not attach`) and only the two-minute
   sweep is left.
4. Whether any *new* code calls a git projection synchronously from a handler.
   The `GitProcessTelemetry` window will name it: the endpoint label will carry
   a non-zero spawn count.
