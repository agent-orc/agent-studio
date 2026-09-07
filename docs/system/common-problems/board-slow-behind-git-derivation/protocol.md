# Root-Cause Protocol

1. Group the `git-info request=` log lines by label and sum `spawns`, `gitMs` and
   `wallMs`. A label with high `wallMs` and `spawns=0` is blocked, not busy.
2. Read `GET /api/admin/performance/git-state`: per-endpoint p50 and p95 over the
   last hour, spawns per minute, and each repository's index age.
3. If spawns per minute is high while nothing is committing, the derivation is
   time-driven rather than change-driven. Check what schedules it.
4. If a request-path rollup shows a non-zero `spawns`, that endpoint is forking
   git itself; it must read the `GitStateIndex` snapshot instead.
5. If spawns are low but a request still waits, look for a blocking wait on a
   thread-pool thread: a synchronous semaphore, `Parallel.ForEach`, or a
   single-flight cache read that joins a background computation.
