---
id: board-slow-behind-git-derivation
title: "Board endpoints are slow while spawning no git themselves"
status: fixed
first-seen: 2026-09-06T20:00:00Z
last-seen: 2026-09-06T20:55:00Z
severity: major
category: performance
tags: [board, git, performance, thread-pool, tasks-grouped, git-inventory]
affects:
  - "GET /api/tasks/grouped"
  - "GET /api/tasks"
  - "GET /api/git/inventory"
related-tasks: [AGT-2726, AGT-2699, AGT-2700, AGT-2701, AGT-2702, AGT-2703]
related-adrs: []
---

# board-slow-behind-git-derivation

**What.** The board takes seconds to answer. `git-info request=tasks/grouped`
rollups show a large `wallMs` with `spawns=0`, so the slow endpoint is not the
one doing git work. Meanwhile the machine runs dozens of git processes a minute.

**Why.** Git-derived state was computed on request paths. The task-list
projection re-derived every two seconds whenever the board was polled, whether or
not a ref had moved, and it did so on the thread pool behind a process-wide
four-slot semaphore acquired with a blocking wait. `tasks/grouped` is a
synchronous handler, so it queued for a thread behind work it had no part in.
`GET /api/git/inventory` forked `worktree list`, `for-each-ref` and a 50-commit
`log` on the request thread behind a three-second TTL.

**Workaround.** None was needed after the fix. While diagnosing an older build,
closing the Project Hub Git view removes the `git/inventory` load and makes the
board usable again.

**Long-term.** Fixed by AGT-2726: one background `GitStateIndex` per repository
owns HEAD, branch tips, worktrees and inventory; request paths read the last
capture and carry a freshness stamp. See
[reports/git-state-index-agt-2726.md](../../reports/git-state-index-agt-2726.md).
