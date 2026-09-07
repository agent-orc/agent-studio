# Related

- Docs: [frontend domain map, Global Search](../../../system/domains/frontend.md#global-search)
  is the system-of-record description of the stream contract, the indexes, and
  the palette's per-domain UX.
- Code: [`GlobalSearchService.cs`](../../../../backend/Features/Search/GlobalSearchService.cs)
  (indexes, sweep, streaming, telemetry),
  [`TaskSearchIndex.cs`](../../../../backend/Features/Search/TaskSearchIndex.cs)
  (task full-text index and its freshness model),
  [`GlobalSearchEndpoints.cs`](../../../../backend/Features/Search/GlobalSearchEndpoints.cs)
  (`/api/search` and `/api/search/stream`).
- Code: [`GitService.MemoizeByHead`](../../../../backend/Features/Git/GitService.cs)
  is the shared HEAD-keyed LRU the file and commit indexes live in. Its
  256-entry limit is shared with every other consumer; a new consumer that
  churns keys can evict the search indexes.
- Code: [`TaskIndexCache`](../../../../backend/Features/Tasks/TaskIndexCache.cs)
  supplies the generation stamp the task index is versioned by. Read its
  consistency model before changing when the search index rebuilds.
- Tests: `backend.Tests/GlobalSearchIndexTests.cs` (no-respawn, no request-path
  file read, stream order, cancellation),
  `frontend/e2e/global-search.spec.ts` (tasks visible while repositories are
  still searching).
- AGT-2722 - dossiers as a search domain, which will ride the same stream.
