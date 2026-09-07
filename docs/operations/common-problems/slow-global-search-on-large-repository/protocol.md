# Root-cause protocol

## Reproducer

1. Open the palette (Ctrl+K) and search a term that matches nothing, for example
   `zzzq`. Note the duration in `global-search-completed`.
2. Search a second, different nonsense term, for example `zzzr`.

**Before AGT-2723:** step 2 costs the same as step 1. The memo key was
`global-search-files|<root>|<query>`, so a new query missed every repository's
entry and re-ran `git ls-files` and `git log` across all of them.

**After AGT-2723:** step 2 is served from memory. `GlobalSearchIndexTests`
asserts this directly by comparing `GlobalSearchService.GitProcessSpawns` across
two queries against one unchanged HEAD.

## How to tell which of the three causes you are looking at

The three causes had the same symptom and different fingerprints in
`global-search-completed`:

| Fingerprint | Cause |
|---|---|
| `tasksMs` dominates, `commitsMs` and `filesMs` small | The task domain is re-reading cards. Check that `TaskSearchIndex` is registered and that `TaskIndexCache.Generation` is not advancing on every read. |
| `filesMs` or `commitsMs` dominates and `repositoryCache` shows `miss` for a repository on every query | That repository's HEAD is unresolvable, so `MemoizeByHead` falls through to an uncached passthrough on every call. |
| `filesMs` or `commitsMs` dominates and `repositoryCache` shows `hit` | Matching itself is slow - an unusually large working tree. The bounded commit window does not bound `git ls-files`. |
| Total is small but the palette still feels stuck | A frontend problem, not a backend one. Check the per-domain status rows and the elapsed clock in the palette. |

## Why the task index is stale-while-revalidate rather than synchronous

`TaskIndexCache.Generation` advances on every published board snapshot, which in
this system means on essentially every mutation. Rebuilding the search index
synchronously on the request path would put a workspace stat sweep in front of a
keystroke. The index therefore serves the current entries immediately and admits
one background rebuild, so the palette can be at most one board mutation behind.
That trade is deliberate: a palette one lane-move stale is better than a palette
that blocks.
