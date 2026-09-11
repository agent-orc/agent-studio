---
name: task-api
description: Programmatic interaction with the agent-taskboard task API over HTTP - create, move, triage, reissue, and reorder tasks when clicking through the board UI would be too slow.
trigger: user
mutates_code: false
mutates_queue: true
sentinel: TASKBOARD-SKILL-TASK-API
---

# Skill: Task-API

> Sentinel: `TASKBOARD-SKILL-TASK-API`. Echo this string in your first reply when the orchestrator probes whether the skill loaded.

Programmatic interaction with the agent-taskboard task API. Read this before
creating or moving tasks via HTTP, especially when the task you are doing
includes "add to the queue", "reissue this task", "drop the task to archive",
"sort the task to the top", or "create a follow-up task".

The board mostly drives state via the UI, but agents (including you, future-me)
need a reliable scripted path because:

- The board cannot create 30 follow-up tasks in a row efficiently.
- Triage of 100+ items belongs in a script, not a click marathon.
- Codex, Claude Code, Copilot, and Gemini all need to be able to do this
  identically; the convention lives here so every CLI picks it up.

## When to invoke

- The user asks for "lege eine Aufgabe an" / "create a task" / "queue a follow-up".
- A triage step needs to move many tasks between lanes.
- You want to surface a finding as a new ticket the orchestrator will pick up.
- You need to promote a hot bug to the top of the queue.

## Server contract

Stable backend listens on `http://127.0.0.1:5031`. Dev backend on
`http://127.0.0.1:5030` (when running). The two have separate workspaces; pick
the one whose project list contains the project you are targeting.

Every mutating request **must** carry the `X-Client-Id: local-default` header.
The `ClientIdentityMiddleware` rejects mutations without it as 401. Read
requests do not need the header but it is harmless to include.

**Use `/api/tasks`.** The route is canonical; the former `/api/jobs`
compatibility alias has been removed (see
[ADR-0057](../../../docs/system/architecture/decisions/adr-archive.md#adr-0057---apijobs-compatibility-alias-removed-route-is-apitasks-only-2026-06-22)).
The raw `watchPath` key and the path-versus-shortCode direction are explained in
[../../../docs/concepts/api-project-identity-and-watchpath.md](../../../docs/concepts/api-project-identity-and-watchpath.md).

## Common pitfall: the watchPath quirk

Every mutation that targets a specific task needs a `watchPath` query parameter.
This is **not** the project's source-tree path; it is the resolved task-folder
root that `GET /api/watch-paths` returns under the `path` field.

For the agent-taskboard project the call is `GET /api/watch-paths`:

```json
[
  {
    "name": "Runbook",
    "path": "C:\\Projects\\agent-taskboard-workspace\\projects\\runbook",
    "rootPath": "C:\\Projects\\Runbook\\App",
    "repositoryPath": "C:\\Projects\\Runbook"
  },
  {
    "name": "Agent Task Processor",
    "path": "C:\\Projects\\agent-taskboard-workspace\\projects\\agent-taskboard",
    "rootPath": "C:\\Projects\\agent-taskboard-devspace\\agent-taskboard-dev"
  }
]
```

Use the `path` field, not `rootPath`. The server resolves tasks against that
path; using `rootPath` returns `409 Job already exists or invalid input` even
when the slug is unique.

For self-contained projects (no `.orchestrator.yml` pointer) the `path` is
`<rootPath>\.orchestrator\jobs`. Always read the live response; never hard-code.

## Hard rules

1. **Always include `X-Client-Id`.** Even on GETs. The Angular interceptor
   adds it for browser requests; the drift rule `frontend-fetch-xclientid`
   exists because three sites forgot it.
2. **Always read `/api/watch-paths` first** if you do not already know the
   exact `path` for your project. Caching it in a script run is fine.
3. **Use Node.js, not curl.** Windows backslashes in JSON bodies break shell
   quoting. The reference scripts in [`scripts/`](scripts) handle this; copy
   them rather than hand-rolling curl.
4. **Create one task per request.** No bulk-create endpoint. Multi-task lane
   moves should use the asynchronous `/api/tasks/batch-move` contract.
5. **Slugs are stable.** Do not include timestamps in `id` unless you actually
   want a fresh per-attempt artifact. Stable slugs keep history linkable.
6. **Lane targets use the full lane name**, e.g. `2-ready` or `6-completed`
   (not `ready` / `completed`). See [`references/states.md`](references/states.md).
7. **Failed-pickup orphans are not regular tasks.** Do not move them with the
   state API if they have no `job.json`. Use a dedicated API recovery/delete
   path when one exists; otherwise stop and ask for an explicit operator
   decision before filesystem cleanup.
8. **`agent` and `cliType` must be real CLI values.** Use `claude`, `codex`,
   `copilot`, or `gemini`, and keep them aligned unless you are deliberately
   testing drift handling. Do not use `agent: "human"` to keep a card visible;
   park visible non-running work by choosing an appropriate lane such as
   `0-backlog` or `5-human-review`.

## Process: finding existing tasks

Before creating a new task, check whether one already exists. The server has
no `?q=`-search endpoint today; it returns the full set and you filter
client-side. The full payload is small (~1-2 MB for hundreds of tasks).

```bash
node scripts/find-tasks.js                       # every task, grouped by lane
node scripts/find-tasks.js --lane 2-ready        # one lane only
node scripts/find-tasks.js --grep "codex"        # id+title contains
node scripts/find-tasks.js --project "Lotta"     # project name contains
node scripts/find-tasks.js --lane 4-auto-review --grep "session"
```

Output: `<lane>\t<project>\t<slug>  -  <title>`, one line per match. Pipe to
`grep` for further narrowing.

Underlying call: `GET /api/tasks/grouped`. Returns
`{ backlog: [], preparation: [], ready: [], progress: [], ... }`, each value
an array of `JobInfo`-shaped objects with `id`, `title`, `projectName`,
`state`, `cliType`, etc.

## Process: creating one task

**Prefer `project` over `watchPath`.** Create accepts a path-free `project`
handle — a `shortCode` / Kürzel (`ASS`) or a `PROJ-NNN` id (from
`GET /api/projects`) — which the server resolves to the project's storage
location. This is the forward-looking contract (watchPath encapsulation, Phase
2a); `watchPath` still works but is deprecated and, when both are sent, `project`
wins. Using `project` also sidesteps the 409 watchPath-mismatch trap below.

```js
const http = require('http');

const task = {
  id: 'my-stable-slug',
  title: 'Human-readable title (shown on the card)',
  targetState: '2-ready',     // initial lane
  order: 0,                    // position within lane; 0 = top of new batch
  taskType: 'bug',             // or feature/refactor/analysis/chore
  agent: 'codex',              // claude | codex | copilot | gemini
  cliType: 'codex',            // keep in lockstep with agent
  project: 'ASS',              // preferred: shortCode/Kürzel or PROJ-NNN id
  // watchPath: '...',         // deprecated fallback; see /api/watch-paths
  promptMarkdown: [
    '## Context',
    '',
    'Markdown lines as a string array, joined with \\n.',
  ].join('\n'),
};

const body = JSON.stringify(task);
const req = http.request({
  hostname: '127.0.0.1', port: 5031, path: '/api/tasks/', method: 'POST',
  headers: {
    'Content-Type': 'application/json',
    'X-Client-Id': 'local-default',
    'Content-Length': Buffer.byteLength(body),
  },
}, res => {
  let d = '';
  res.on('data', c => d += c);
  res.on('end', () => console.log('status:', res.statusCode, '| body:', d.slice(0, 200)));
});
req.on('error', e => console.log('ERR', e.message));
req.write(body); req.end();
```

A 200 with `{"id":"..."}` means success. A 409 with `"Job already exists or
invalid input"` is the most common failure; nine times out of ten the cause is
the `watchPath` not matching `/api/watch-paths`.

The full template is in [`scripts/create-task.js`](scripts/create-task.js).

## Process: moving a task between lanes

`POST /api/tasks/{jobId}/move?watchPath=...` with body `{"targetState":"6-completed"}`.

```js
const body = JSON.stringify({ targetState: '6-completed' });
const path = `/api/tasks/${encodeURIComponent(jobId)}/move` +
             `?watchPath=${encodeURIComponent(watchPath)}`;
// POST, same headers as create
```

Common targets:
- `6-completed` (logically completed; physically lands under `7-archive`)
- `2-ready` (re-queue / reissue)
- `5-human-review` (parking lot for "I need your call")
- `7-archive` (drop without ceremony)

The full template is in [`scripts/move-state.js`](scripts/move-state.js).

For more than one independent move, send one `POST /api/tasks/batch-move`
request with an `items` array. The endpoint returns `202` and a job handle
immediately. Poll `GET /api/tasks/batch-move/{id}` until `status` is
`completed` or `failed`. A failed item appears in `results` and does not stop
the rest of the batch.

## Process: releasing a task for its gated dependents

`PUT /api/tasks/{jobId}/release?watchPath=...` with body `{"released": true}`.

A `references.dependsOn` edge written as `{"key": "AGT-2372", "releaseGate": true}`
is only fulfilled once the target is terminal **and** carries this explicit flag.
Reaching `6-completed` never sets it; the approval is a separate decision. Until
it is set the dependent stays on the board reading `waits for release: <key>` and
is not auto-picked.

```js
const body = JSON.stringify({ released: true });   // false withdraws the release
const path = `/api/tasks/${encodeURIComponent(jobId)}/release` +
             `?watchPath=${encodeURIComponent(watchPath)}`;
// PUT, same headers as create
```

Notes:

- `jobId` is the **target** (the task being released), not the dependent.
- The call is reversible and idempotent in effect; each call appends one
  `task_released` timeline event with the acting `X-Client-Id`, so send a real
  identity rather than a shared one when the audit trail matters.
- Operators do not need this endpoint: the task detail's References section has
  a **Release for dependents** action on the target and an inline **Release**
  on the dependent, and the board filter panel has a **Waiting for release**
  facet. Use the API for scripted release steps and bulk work.

## Process: promoting to the top of `2-ready`

`POST /api/tasks/{jobId}/move-to-top?watchPath=...` with no body.

Use this when a hot bug needs to be picked up before the existing queue, but
the task is already in `2-ready` or `0-backlog`. The state machine handles the
atomic reorder on disk.

Reference: [`scripts/move-to-top.js`](scripts/move-to-top.js).

## Process: bulk triage

Triage rolls separate classification from mutation. Pattern:

1. Read the folder contents via `fs.readdirSync`, classify each entry
   (status.md + aspect-*.md) into action buckets.
2. Apply any prompt edits independently, then submit each move bucket through
   `POST /api/tasks/batch-move` and poll its job handle to completion.
3. Idempotent prompt-append: when reissuing, check if the prompt.md already
   contains your "Human Review Note" marker so re-running the script does not
   stack duplicate notes.

The full reusable triage script is in [`scripts/triage-lane.js`](scripts/triage-lane.js).

## Output expectations

When you run any of these scripts, report the count of successes + failures
and any unusual statuses. A silent `200` is fine; a `409` or `404` needs to be
shown to the user with the offending slug so they can dedupe.

For larger triage operations, also produce a one-line summary per bucket
("moved 45 to completed, 12 reissued to ready, 3 unclear").

## Anti-patterns

- **Curl with inline JSON strings.** Backslashes in Windows paths break shell
  quoting. The two times I tried it in the same session both failed; the Node
  template never failed.
- **Hard-coding `rootPath` as `watchPath`.** Returns 409 every time.
- **Forgetting `X-Client-Id`.** The 401 returns no body, and the error message
  is dropped on the floor in some calling code paths.
- **Bypassing the API by editing folders directly.** The in-memory cache stays
  stale until invalidation; only do this for shell folders that the API
  cannot see (e.g. an orphan with no `job.json`).
- **Hand-rolling YAML escapes for prompt content.** Use the `[...].join('\n')`
  pattern so multiline markdown stays readable in source.
- **Creating duplicate slugs for "test" tasks.** They land as 409 conflicts.
  Add a suffix only if you want a fresh attempt artifact, never as a habit.

## Reference

- [`scripts/find-tasks.js`](scripts/find-tasks.js) - list / filter / search across all projects
- [`scripts/create-task.js`](scripts/create-task.js) - create one task (template)
- [`scripts/move-state.js`](scripts/move-state.js) - move task to another lane
- [`scripts/move-to-top.js`](scripts/move-to-top.js) - promote to head of `2-ready`
- [`scripts/triage-lane.js`](scripts/triage-lane.js) - bulk-classify + move
- [`references/states.md`](references/states.md) - full lane vocabulary
- [`references/endpoints.md`](references/endpoints.md) - full endpoint surface
- [`references/known-pitfalls.md`](references/known-pitfalls.md) - bugs and
  drift patterns observed in past sessions
