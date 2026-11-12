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

Promote a job to the head of `2-ready`. For a card in `4-auto-review`, set
urgent review priority instead. No body. Requires `X-Client-Id`. Returns
`{ position: 0 }` for Ready or `{ position, priority: "urgent" }` for AutoReview;
returns `404` when the card is not in either lane.

### `POST /api/tasks/{jobId}/review-priority?project=...`

Set an AutoReview card's priority with `{ "priority": "urgent" }` or
`{ "priority": "normal" }`. Requires `X-Client-Id` and records the operator
in the card ledger. Returns `{ priority, position }`, where `position` is null
until the review attempt is claimable. Within each priority, claims stay FIFO.

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

Errors: `400` "Invalid mode '<mode>'. Allowed: manual, auto-single,
auto-continuous, paused." for an unknown mode; `409` "has no RootPath
configured" when the project has no working directory and therefore no local
runner; `404` "Unknown project" for an unknown name. Project Settings uses
`PUT /api/projects/{project}/execution-runner` with `pickupMode` `auto`,
`manual` or `paused` instead; `auto` maps to `auto-continuous`.

## Bus / observability (read-only)

- `GET /api/bus/{project}/summary` - aggregate counts
- `GET /api/bus/{project}/recent?limit=N` - latest N messages
- `GET /api/bus/{project}/messages?...filters...` - filtered query
- `GET /api/bus/{project}/token-aggregate?since=&until=` - canonical token roll-up

Use these after triggering work to verify it actually landed.

## Operator

Routes for an operator agent: the coding-agent CLI session the owner runs
to operate Studio (operator agent level Dossier, OP-2). In the networked
profile they accept a human session, an API token, or an operator agent
session as `Authorization: Bearer`. Each caller receives only events of the
projects it may read.

### `GET /api/operator/decisions?after=<cursor>&limit=<n>`

Snapshot of the decision stream after `after`, oldest first (`limit` 1 to
1000, default 200). Without `after` it returns every event still retained.

```json
{
  "events": [{
    "id": "opd_...", "cursor": "42", "type": "escalation.opened",
    "time": "...", "project": "demo", "cardKey": "DEMO-9", "cards": [],
    "reason": "...", "suggestedOperations": ["card.inspect", "card.continue", "card.move"],
    "evidence": [{ "label": "card", "href": "/api/tasks/DEMO-9" }],
    "fact": "escalation.opened|demo|DEMO-9|...", "observedAt": "..."
  }],
  "cursor": "42", "headCursor": "42", "oldestCursor": "1",
  "hasMore": false, "gap": false, "retentionHours": 72
}
```

Store `cursor` and pass it as `after` next time. `gap: true` means events
after that cursor were dropped by retention (at least 24 hours,
`OperatorDecisions:RetentionHours`, default 72) or the cursor comes from
another installation. `observedAt` is when the underlying fact began; `time`
is when the stream published it.

Event types, the fact each one publishes, and the suggested operations:

| Type | Published when | Suggested operations |
|---|---|---|
| `escalation.opened` | A card enters `5e-escalated` | `card.inspect`, `card.continue`, `card.move` |
| `human-review.ready` | A card enters `5-human-review` | `card.inspect`, `card.accept`, `card.continue` |
| `decision-card.opened` | A decision card is pending | `card.inspect`, `owner.decide` |
| `backend.stalled` | The watcher opened a stall case of kind `host-offline`, `host-overload`, `memory-pressure`, `deploy-drift`, `claim-refusal`, or `batch-gate-stuck` | `stalls.inspect`, `capacity.read` |
| `progress.idle-with-ready` | The watcher opened a stall case of kind `idle-capacity` or `fleet-claim-stall` | `stalls.inspect`, `capacity.read` |
| `batch.red` | A batch gate flight turned red | `batch.triage`, `batch.eject` |
| `batch.isolating` | A red flight has diagnostic child batches still running | `batch.triage`, `batch.eject` |
| `batch.record-unreadable` | A batch manifest, state or run record does not deserialize; the store quarantined it. The reason names the batch, project, file, parse error and next step | `batch.triage` |
| `push.pending-overdue` | A pending integration push passed `BatchGate:PendingPushAlertMinutes` | `push.inspect`, `batch.triage` |
| `push.blocked` | A Post Processing card merged locally but its publication to origin was refused | `card.inspect`, `push.inspect` |
| `quota.exhausted` | A card waits for a quota reset, or a CLI hit its provider limit (installation-wide) | `capacity.read`, `routing.change` |
| `quota.fallback-active` | A running card uses a quota fallback | `capacity.read`, `routing.change` |
| `watcher.heartbeat` | The watcher lease was granted, taken over, renewed, or released | `watcher.status` |

Each occurrence is published once. A fact that ends and returns later (a
card escalated again) is a new event.

### `GET /api/operator/decisions/stream`

The same events as Server-Sent Events: `id:` is the cursor, `event:` the
type, `data:` the event JSON. Resume with `?after=<cursor>` or the
`Last-Event-ID` header; every event after it arrives once, in order. Without
a cursor the stream starts at the head and sends only new events. A
`stream.gap` frame first means the cursor was older than the retained events.
A `: keep-alive` comment follows every 15 seconds without events.

### `GET|POST /api/operator/watcher/lease`

One watcher per installation. `POST` with
`{ "session": "<label>", "takeOver": false, "reason": "..." }` returns
`{ "outcome": "granted", "lease": { "leaseId", "holder", "session", "lastHeartbeatUtc", "expiresAtUtc", "heartbeatSeconds" } }`.
While another watcher holds an active lease the answer is
`409 { "error": "watcher-active", "holder", "session", "lastHeartbeatUtc" }`.
Only an explicit `"takeOver": true` replaces the holder; the take-over is
recorded in the control plane audit with the previous holder.

- `POST /api/operator/watcher/lease/{leaseId}/heartbeat` renews the lease;
  send it every `heartbeatSeconds`. A lease without a heartbeat for
  `OperatorDecisions:WatcherLeaseSeconds` (default 90) expires.
  `409 watcher-lease-lost` names the new holder after a take-over.
- `DELETE /api/operator/watcher/lease/{leaseId}` releases it.
- Viewer tokens may read the lease and the stream but not take the lease.

## Operator access (networked profile)

An operator agent or script authenticates with an API token the Owner creates
under `POST /api/auth/api-tokens`:

```json
{ "name": "operator laptop", "role": "operator", "projects": ["*"] }
```

`expiresAt` is optional: without it the token expires after 30 days, and it
can be at most 365 days away. The response shows the `api.` secret once. Store
it in `~/.config/agent-studio/operator-token` with `chmod 600`; the reference
scripts read it only from there (see the skill's server contract).

An `operator` token binds the `operator-agent` role, shown under that name in
Settings > Access. It reaches the operator surface:

| Need | Routes |
|---|---|
| Board, cards, decisions | `GET /api/tasks/grouped`, `GET /api/tasks/{id}`, `GET /api/runner/{project}/pending-decisions`, `GET /api/projects/{project}/review-decisions-pending`, `GET /api/watcher/*`, `GET /api/runner/orchestrator-feed` |
| Batch gate | `GET /api/projects/{project}/batch-gate/state`, `/report`, `/queue-stalls`; `POST .../batch-gate/members/{key}/eject`, `POST .../batch-gate/close-now` |
| Continue and steer | `POST /api/tasks/{id}/continue` (`mode`: `continue` or `steer`) |
| Lane moves and acceptance | `POST /api/tasks/{id}/move`, `POST /api/tasks/batch-move` |
| Create and title | `POST /api/tasks`, `PUT /api/tasks/{id}/title` |
| Review step models | `PUT /api/projects/{project}/pipeline-step` |
| Quota and routing | `GET /api/cli/quota`, `GET /api/cli/quota/model-routes` |
| Deploys | `GET /api/deploy/matrix`, `GET /api/projects/{project}/deploy/*`, `POST /api/projects/{project}/environments/releases` (stage) |

It is refused with 403 for secret writes and secret value reads, production
approvals and production deploys, access administration (`/api/auth/*`), and
installation-wide settings. An operator override completion
(`operatorOverride` on a move to `6-completed`) and decision cards stay with
the Owner. A project-scoped token reaches only its projects.
