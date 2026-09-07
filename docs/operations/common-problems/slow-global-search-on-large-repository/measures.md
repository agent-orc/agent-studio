# Measures

Fix attempts and their status. Status vocabulary: `tried`, `applied`, `works`, `regressed`.

| Status | Date (UTC) | Measure | Owner | Outcome |
|---|---|---|---|---|
| works | 2026-09-07 | HEAD-keyed cache keys for the file and commit indexes drop the query string; matching moved in-memory | AGT-2723 | A second query against an unchanged HEAD spawns no git process, asserted by `GlobalSearchIndexTests.SecondQueryOnTheSameHead_SpawnsNoGitProcess` |
| works | 2026-09-07 | `TaskSearchIndex` replaces per-query `prompt.md` / `status.md` reads with an in-memory index versioned by `TaskIndexCache.Generation`, rebuilt in the background, with per-card text memoized by document length and last-write | AGT-2723 | The task domain answers with no request-path file read, asserted by `TaskDomain_AnswersFromMemoryWithoutReadingTheCardOnTheRequestPath` |
| works | 2026-09-07 | `git log` bounded to a 2,000-commit window per repository (`GlobalSearchService.CommitWindow`) | AGT-2723 | Caps the first-sweep cost on a long history; a match older than the window is not returned |
| works | 2026-09-07 | Repositories swept in parallel with a bounded degree (`ProcessorCount / 2`, clamped to 2..6) | AGT-2723 | The sweep is no longer serialised across roughly fifteen checkouts |
| works | 2026-09-07 | `GET /api/search/stream` delivers per domain and per repository; the palette debounces 250 ms, aborts on every keystroke and on Escape, and shows per-domain status plus an elapsed clock | AGT-2723 | The operator sees what is searching and how far; a cancelled search stops its work |
| works | 2026-09-07 | `global-search-completed` carries per-domain durations and a per-repository cache hit/miss report; a search over 5 s logs `global-search-slow` naming the slowest repository | AGT-2723 | A recurrence names the offending repository instead of requiring a profiler |

## Operator knobs and signals

- **Which repository is slow.** Grep the backend log for `global-search-slow`.
  It carries `slowestProject`, `slowestDomain`, `slowestMs`, and whether that
  repository was a cache hit.
- **Whether the cache is working.** `global-search-completed` carries
  `repositoryCache=<project>=hit|miss,...`. Repeated `miss` for the same
  repository across queries with no commits in between means HEAD resolution is
  failing for it - check that the path is a git repository at all.
- **Commit window.** `GlobalSearchService.CommitWindow` (2,000). Raising it
  raises the first-sweep cost linearly; it is not a per-query cost.
