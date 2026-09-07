# Occurrences

| When (UTC) | Task / context | Agent / CLI | Affected paths | Notes |
|---|---|---|---|---|
| 2026-09-06T20:00:00Z | Operator laptop running Studio, the Task Server and the review gates | Backend | `GET /api/tasks/grouped`, `GET /api/git/inventory` | 55-minute window: about 3,980 git processes, roughly 72 a minute; `tasks/grouped` 2,775 s of wait with zero spawns; worst single request 95.8 s. |
