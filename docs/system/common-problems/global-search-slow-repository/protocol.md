# Root-cause protocol

Confirm the shape before tuning anything:

1. Read the `global-search-completed` log line. Its `cache=` field lists every
   repository as `name=commits/files:duration`, so a cold corpus (`miss`) is
   distinguishable from a genuinely slow repository that was already warm.
2. A search over five seconds also emits `global-search-slow` naming the
   slowest repository and whether it was served from cache.
3. If the slow repository is `miss` on the first search after a push and `hit`
   afterwards, this is the bounded log-window build and the entry applies.
4. If it is slow while `hit`, the cost is not the git spawn: look at the
   in-memory match instead, and at how many paths that checkout carries.
