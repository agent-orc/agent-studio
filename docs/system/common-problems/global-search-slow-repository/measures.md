# Measures

Fix attempts and their status. Status vocabulary: `tried`, `applied`, `works`, `regressed`.

| Status | Date (UTC) | Measure | Owner | Outcome |
|---|---|---|---|---|
| works | 2026-09-06 | Key the file and commit corpora by HEAD alone instead of by (repository, query), so a new search term reuses the index. | AGT-2723 | A second query against an unchanged repository spawns no git process. |
| works | 2026-09-06 | Stream per-domain and per-repository, so the palette is never blocked by the slowest checkout. | AGT-2723 | Task matches render immediately; repositories append as they answer. |
| applied | 2026-09-06 | Bound the commit index with the configurable `Search:CommitWindow` window. | AGT-2723 | Default 2000 commits per repository per HEAD. |
