# Occurrences

| When (UTC) | Task / context | Agent / CLI | Affected paths | Notes |
|---|---|---|---|---|
| 2026-09-06T18:00:00Z | Palette timing measurements | Claude | `backend/Features/Search/GlobalSearchService.cs` | Live calls: `AGT-W15` 17.2 s, `quota` 19.6 s, `runner` 22.0 s. One spinner for the whole duration. |
| 2026-09-06T22:00:00Z | `global-search-completed` log sample | Claude | Backend log | Same evening: 1.0 s, 3.4 s, 6.3 s, 8.5 s, 29.1 s, 36.3 s. Spread tracks which repositories had moved HEAD. |
