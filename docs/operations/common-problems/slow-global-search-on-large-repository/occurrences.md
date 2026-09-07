# Occurrences

Chronological log. Newest at the top. UTC timestamps. One row per observation.

| When (UTC) | Task / context | Agent / CLI | Affected paths | Notes |
|---|---|---|---|---|
| 2026-09-06T21:00:00Z | `global-search-completed` log sweep over one evening | backend log | `backend/Features/Search/GlobalSearchService.cs` | Durations 1.0 s, 3.4 s, 6.3 s, 8.5 s, 29.1 s, 36.3 s. Spread tracks how many repository HEADs had moved since the previous query, not query complexity. |
| 2026-09-06T18:00:00Z | Live palette calls from the studio shell | manual | `frontend/src/app/features/studio-shell/components/global-search` | `AGT-W15` 17.2 s (3 tasks, 0 commits, 0 files), `quota` 19.6 s, `runner` 22.0 s. One spinner for the whole duration; no way to tell working from hung. |
