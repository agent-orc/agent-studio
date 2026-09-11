# Task API Endpoint Reference

Every endpoint listed here is on the backend (`http://127.0.0.1:5031` for
stable, `:5030` for dev). Mutations require the
`X-Client-Id: local-default` header. Source: `backend/Endpoints/Tasks/*.cs`.

## Discovery

### `GET /api/watch-paths`

List of watched projects with the path you must pass as `watchPath` on every
mutation. **Always read this once** at the start of a script; the `path`
field is the canonical resolved path, not the `rootPath`.

Response shape:

```json
[
  { "name": "Agent Task Processor",
    "path": "C:\\Projects\\agent-taskboard-workspace\\projects\\agent-taskboard",
    "rootPath": "C:\\Projects\\agent-taskboard-devspace\\agent-taskboard-dev",
    "repositoryPath": "" }
]
```

### `GET /api/runner/status`

Per-project runner snapshot. Useful before mutating to check whether the
project is busy (avoid disturbing an in-flight run).

```json
{
  "projects": {
    "Agent Task Processor": {
      "mode": "auto-continuous",
      "activeJobId": "fix-bar",
      "activeExecution": { "status": "running", "startedAt": "..." },
      "queuedJobIds": ["..."]
    }
  }
}
```

## Listing tasks

### `GET /api/tasks/grouped`

Returns jobs grouped by lane. Heavy; prefer the per-lane folder scan when you
only need slugs.

The `archive` bucket is **always `[]`** here by design (ASS-1727): the
cache-backed board scan excludes the terminal `7-archive` lane so a poll never
carries hundreds of terminal cards. Read the archive through the dedicated
paged endpoint below, not this field.

### `GET /api/tasks/archive`

Paged, slim read of the terminal `7-archive` lane (ASS-1727). The board
`/grouped` response keeps `archive` empty, so the Archive view lazy-loads
through here. Reuses the slim-hydrated archive partition the index cache
already built from its single shared disk walk - no per-request full scan.

Query params (all optional):

| Param | Default | Notes |
| --- | --- | --- |
| `watchPath` | all projects | restrict to one project's archive |
| `offset` | `0` | paging start; negative is clamped to `0` |
| `limit` | `50` | page size, clamped to `1..200` |
| `search` | none | case-insensitive substring over title / key / id |
| `includeFixtures` | `false` | include fixture jobs (mirrors the board endpoints) |

Ordering is newest-archived first (`enteredLaneAt` desc, `lastActivity` as
tiebreaker). `total` is the full unpaged count for the current filter, so a
search narrows `total` to the match count. Response shape:

```json
{
  "items": [
    { "id": "fix-foo", "key": "ASS-123", "title": "Fix foo",
      "state": "7-archive", "projectName": "Agent Task Processor",
      "enteredLaneAt": "2026-06-01T10:00:00Z", "lastActivity": "...",
      "commitCount": 3, "codeActivityDetected": true }
  ],
  "total": 852,
  "offset": 0,
  "limit": 50
}
```

A search query logs the stable event `task-archive-search`
(`term`, `matched`, `archivedScanned`) so a "filter found nothing" report is
diagnosable from the api log.

### `GET /api/tasks/{id}`

Single job detail. Pass `?watchPath=...` to disambiguate when the slug exists
in multiple projects.

## Mutations (all require X-Client-Id)

### `POST /api/tasks`

Create a job. Body:

```json
{
  "id": "stable-slug",
  "title": "Card title",
  "targetState": "2-ready",
  "order": 0,
  "taskType": "bug",
  "agent": "codex",
  "cliType": "codex",
  "watchPath": "C:\\Projects\\agent-taskboard-workspace\\projects\\agent-taskboard",
  "promptMarkdown": "..."
}
```

`agent` and `cliType` must be supported CLI values (`claude`, `codex`,
`copilot`, or `gemini`) and should match. Do not use `agent: "human"` as a
parking mechanism; choose a non-running lane instead.

Returns `200 {"id":"..."}` or `409 "Job already exists or invalid input"`.

### `PUT /api/tasks/{jobId}/state?watchPath=...`

Move a job to another lane. Body:

```json
{ "targetState": "6-completed" }
```

Returns `200` (empty body). 404 if slug not found at the resolved watchPath.

### `POST /api/tasks/{jobId}/move?watchPath=...`

Same effect as `PUT /state`; exists as an alternative verb for clients that
prefer POST for state changes.

### `POST /api/tasks/batch-move`

Queue up to 500 independent state transitions without occupying the request
path for their filesystem and Git work. The body is:

```json
{
  "items": [
    { "jobId": "alpha", "watchPath": "C:\\Tasks\\project", "targetState": "7-archive" },
    { "jobId": "beta", "watchPath": "C:\\Tasks\\project", "targetState": "2-ready" }
  ]
}
```

The response is `202 Accepted`, carries a job snapshot, and sets `Location` to
the progress endpoint. `status` may already be `running` when the response is
read.

```json
{
  "id": "batch-...",
  "status": "queued",
  "total": 2,
  "completed": 0,
  "succeeded": 0,
  "failed": 0,
  "results": []
}
```

### `GET /api/tasks/batch-move/{batchId}`

Read the latest job snapshot. While `status` is `running`, `completed` and
`results` grow after every card. Each result contains `index`, `jobId`,
`status`, optional `message`, and `durationMs`. Item statuses include `moved`,
`not-found`, `conflict`, `rejected`, and `failed`. One item error never stops
later items. Terminal snapshots also contain `metrics` for total and per-item
move duration, lane-lock acquisitions/wait/held time, scanner invalidations
and refresh time, and Git process count/time.

### `POST /api/tasks/{jobId}/move-to-top?watchPath=...`

Promote a job to the head of `2-ready`. No body. Returns
`{ position: 0 }` on success, `404` if not found in a promotable lane.

### `POST /api/tasks/reorder`

Bulk reorder. Body:

```json
{ "jobIds": ["slug-a", "slug-b", "slug-c"] }
```

The given order becomes the new order on the lane the jobs share. All jobs
must be on the same lane.

### `DELETE /api/tasks/{jobId}?watchPath=...`

Delete a job and its folder. Returns `200` on success.

### `DELETE /api/tasks/orphan-folder`

Delete a scanner-invisible residue folder in a terminal lane. This is for
folders that have no `job.json`, such as boot-sweep leftovers in `7-archive`.
Body:

```json
{
  "watchPath": "C:\\Projects\\agent-taskboard-workspace\\projects\\agent-taskboard",
  "lane": "7-archive",
  "folder": "kanban-lane-grouping-collapse-empty-2026-05-05"
}
```

Allowed lanes are `7-archive` and `6-completed`. The endpoint refuses
non-terminal lanes, path traversal folder names, and any folder that contains
`job.json`; use normal task deletion for real tasks. Success returns
`200 { "status": "deleted" }`. The backend emits the stable structured log
events `task-orphan-folder-deleted` and `task-orphan-folder-delete-failed`.

### `POST /api/tasks/{jobId}/restore-from-failed-pickup?watchPath=...`

Lift a folder out of `3a-failed-pickup` back into `2-ready` and drop the
`-pickup-failed-<utc>` suffix in one server-side step. Pass the dead-letter
slug as `{jobId}` (e.g. `foo-pickup-failed-2026-05-08`). Body is optional:

```json
{ "keepDeadLetterSlug": true }
```

retains the suffix; omit the body (or `false`) to recover the original slug.
Idempotent (`200 { "status": "no-op" }` when already restored). Appends a
`pickup-restored` row to `<workspace>/logs/pickup-failures.jsonl`. Use this
instead of a manual `mv` + rename.

### `PUT /api/tasks/{jobId}/title`, `.../task-type`, `.../cli-type`, `.../model`

Single-field edits. Body: `{ "title": "new" }`, `{ "cliType": "codex" }`,
`{ "model": "gpt-5.5" }`, etc. Updating `cli-type` also keeps the parallel
`agent` field in lockstep.

### `PUT /api/tasks/{jobId}/release?watchPath=...`

Set or withdraw the explicit content release consumed by `references.dependsOn`
edges written as `{ "key": "...", "releaseGate": true }`. Body:
`{ "released": true }` (`false` withdraws). `jobId` is the **target** being
released, not the dependent waiting for it.

Terminal completion never sets this flag, so a gated dependent stays blocked
(`waits for release: <key>` on its card) until this call lands. Each call
appends one `task_released` timeline event attributed to the request's
`X-Client-Id`, together with the release-gated dependents it moves.

`404` when the job id / watchPath pair does not resolve.

### `POST /api/tasks/{jobId}/intake`

Trigger the orchestrator-intake gate for a single job (when configured).

### `POST /api/tasks/{jobId}/commits/integration?watchPath=...`

Append an already-existing integration commit to the task's `commits[]` chain
without creating or rewriting a Git commit. The singular `commit` field is
mirrored to the appended final entry.

```json
{ "sha": "0123456789abcdef0123456789abcdef01234567" }
```

The SHA must be the full 40-character object id, must resolve in the task's
repository, and the commit message must name the task key. Repeating the same
request refreshes that entry in place instead of duplicating it. Returns the
refreshed `commit` and `commits` values.

### `POST /api/tasks/{jobId}/integration-records?watchPath=...`

Append one operator- or GPT-reviewed historical integration classification.
The write is bookkeeping only and cannot change a lane or Git state. Use a
stable campaign id so retries are idempotent.

```json
{
  "id": "operator-gpt-verification-2026-08-11",
  "classification": "integrated-verified",
  "acceptedAtUtc": "2026-08-11T10:00:00Z",
  "integrationBranch": "main",
  "commitShas": ["0123456789abcdef0123456789abcdef01234567"],
  "fenceRefs": [],
  "evidence": "Git ancestry and task evidence reviewed on 2026-08-11."
}
```

`classification` must be `integrated-verified`, `integrated-historical`,
`no-code-expected`, `content-on-fence`, or `genuinely-missing`. The endpoint
returns `409` for Preparation, Ready, Progress, and Post Processing cards so a
current delivery wave remains untouched. Human Review, Escalated, Completed,
and Archive cards are eligible. A repeated `id` returns `200` with
`appended: false`.

## Runner mode

### `PUT /api/runner/{projectName}/mode`

Switch a project's runner mode. Body:

```json
{ "mode": "auto-continuous" }
```

Valid modes: `manual` | `auto-single` | `auto-continuous` | `paused`.

Note: returns `400 "Invalid project or mode"` when the project has no
registered runner. This currently happens when a watch-path is added after
backend start; restart picks it up. See
`fix-runner-mode-rejects-newly-added-projects` if you hit this.

## Bus / observability (read-only)

- `GET /api/bus/{project}/summary` - aggregate counts
- `GET /api/bus/{project}/recent?limit=N` - latest N messages
- `GET /api/bus/{project}/messages?...filters...` - filtered query
- `GET /api/bus/{project}/token-aggregate?since=&until=` - canonical token roll-up

Use these after triggering work to verify it actually landed.
