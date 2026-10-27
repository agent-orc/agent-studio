# Board read model on the Task Server: shadow, then cutover

Migration steps M1 and M2 of the pipeline delivery target
([Dossier, section 05](../pipeline-delivery-target/index.html)), decision D7
option A-B: the Task Server answers card state and the board from its `tasks`
table, so a board read neither scans task folders nor spawns Git. Implemented
by AGT-3317.

## Moving parts

| Part | Where | What it does |
|---|---|---|
| Board read model | Task Server `GET /api/v1/projects/{projectId}/board` | Lanes in lifecycle order, card core (id, key, title, lane, rank, version, archive state), per-lane counts, archived count. `ETag` is `"board-v1-g{generation}"`; a matching `If-None-Match` answers 304 before the rows are read. `{projectId}` is the project id or name. Scope `tasks:read`. |
| Change feed | Task Server `GET /api/v1/projects/{projectId}/board/changes?after={generation}&limit=` | Board changes after a generation (`upsert` or `remove`, task id, key and state). `resyncRequired: true` when the cursor is ahead of the server or older than the retained log (20,000 rows); the client then reads the board again. |
| Generation | Task Server triggers on `tasks` | Every insert, delete, and change of project, key, title, state, rank or archive state appends one `board_changes` row and advances the project's generation, whichever writer made it. A transaction can advance it by more than one; readers only see committed generations. Body-only edits do not move it. |
| Workspace import | Task Server `PUT /api/v1/projects/{projectId}/board/workspace-import` | Card core from the workspace index while the workspace is still the write authority. Keys are upper-cased; new cards get the legacy importer's deterministic id. A key owned by another project, or the removal of a card with run history, is rejected and the row is kept. Scope `tasks:write`. |
| Mirror and comparison | Backend `BoardShadowWorker` | One serialized cycle: send the delta of the newest published workspace index generation, then, if a board read asked for it, compare that same generation with the read model. Board reads never wait for it in shadow mode. |
| Metrics card | Backend `GET /api/admin/board-source`, Studio Settings > Prompts, "Board source" card | Source, mirror generation and last error, comparisons per board read, differences by card and field, board read p95 and Git spawns per read against the targets, cutover state. |

## Setting

`Board:Source` in the backend configuration, read on every board read:

| Value | Board answer | Task Server |
|---|---|---|
| `workspace` (default) | Workspace task index, as before. | Untouched. |
| `task-server-shadow` | Workspace task index. | Mirrored; each board read asks for one comparison, coalesced off the request path. |
| `task-server` | Lanes, membership, title and order from the Task Server; the workspace index only enriches cards it also holds (runtime, review and Git signals). | Still mirrored, so writes through the monolith API reach both stores for this release. |

Rollback is the setting: set `Board:Source` back to `workspace`. A value the
backend does not know falls back to `workspace` and the card shows it as rejected.

Related keys: `Board:TaskServer:BaseUrl` (falls back to `TaskServer:BaseUrl`;
host must be in the Task Server's `AllowedHosts`), `Board:TaskServer:AuthToken`
or `Board:TaskServer:AuthTokenFile` (a principal with `tasks:read` and
`tasks:write`), `Board:Shadow:SyncIntervalSeconds` (default 5),
`Board:Shadow:StatePath` (default `<TaskRepository>/logs/board-shadow-ledger.json`,
outside committed workspace content).

Every grouped board response carries `X-Board-Source` and `X-Board-Git-Spawns`
(the Git spawns of that read). In `task-server` mode it also carries
`X-Board-Source-Degraded`: `mirror-behind` when the mirror missed its 2 second
wait, `{project}:unavailable` when the last model was served because the Task
Server was unreachable, and `{project}:missing` when the Task Server does not
know the project. The board never falls back to the workspace in that mode.

## Procedure

1. **M1 import.** Import the workspace into the Task Server with the existing
   inventory and `import` commands
   ([runbook](remote-task-server-local-studio.md)), so every workspace project
   exists there. The mirror does not create projects; a missing project is
   listed on the card and compared as a `project` difference.
2. **M1 shadow.** Set `Board:Source = task-server-shadow`. Watch the card: the
   mirror generation follows the workspace and comparisons follow board reads.
3. **Explain or fix every difference.** Each difference names the card and the
   field (`presence`, `lane`, `title`, `order`, `count` per lane,
   `archived-count`, `project`). Fix the cause where it is a defect. Where it is
   expected, record the reason with **Explain** on the card or
   `POST /api/admin/board-source/differences/explain` with
   `{ "fingerprint": "...", "explanation": "..." }`. An explanation is dropped
   when the same field later disagrees with other values.
4. **Gate.** The card reads `Ready for task-server` after 7 days of comparisons
   with no unexplained difference seen. A day without a comparison restarts the
   window, and so does an unexplained difference.
5. **M2 cutover.** Set `Board:Source = task-server` for Agent Studio. Check on
   the card that board read p95 stays under 200 ms and Git spawns per read stay
   at 0.
6. **M3** (separate card) moves the write authority. Until then writes go
   through the monolith API and reach both stores through the mirror. Task
   files and results follow the same pattern with `TaskArtifacts:Source`; see
   [workspace repository as export](workspace-repository-lifecycle.md#workspace-repository-as-export)
   (AGT-3320).
