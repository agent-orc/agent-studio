# Measures

| Status | Date (UTC) | Measure | Owner | Outcome |
|---|---|---|---|---|
| works | 2026-09-07 | Drop the query string from the HEAD-keyed cache key so a new search term reuses the index instead of re-spawning `git ls-files` and `git log` per repository. | Platform | AGT-2723 |
| works | 2026-09-07 | Bound the commit index to the newest 2,000 commits and cap the file index, logging truncation rather than trimming silently. | Platform | AGT-2723 |
| works | 2026-09-07 | Stream per repository so a slow checkout degrades one row instead of the whole palette, and cancel kills the git children. | Platform | AGT-2723 |
| open | | Warm the git indexes off the request path the way the task blob is warmed. | Platform | |
