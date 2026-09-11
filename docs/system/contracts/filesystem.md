# Filesystem Contract

## Where Jobs Live

For every newly onboarded project, task metadata and evidence live in the
central `TaskRepository`, never in the product repository. This is the
canonical layout:

```text
<TaskRepository>/
  .metadata/
    projects.json
    workspaces.json
    migrations/
      superseded-commits-v1.json
      historical-integration-verification-v1.json
      historical-integration-verification-v2.json
    test-runs/
      PROJ-023.json
  projects/
    PROJ-023/
      tasks/
        0-backlog/
        1-preparation/
        ...
```

The product checkout is a separate location referenced by `RepositoryPath`.
It contains source code and the project's own `docs/` tree, but Agent Studio
must not create `.orchestrator/jobs`, task prompts, logs, attachments, results,
or task metadata there.

Older projects may still resolve an in-repository `.orchestrator/jobs` folder,
or may use an `.orchestrator.yml` pointer to the legacy
`<TaskRepository>/projects/<projectKey>/` layout. Those are compatibility-only
layouts. Do not create either layout during onboarding and do not copy a
central task store into a product checkout. Move a legacy store only through a
controlled migration that updates its registry record.

`RootPath` is the implementation working directory for CLI runs and pointer lookup. A watch entry may also set `RepositoryPath` when Git operations should run from a different directory, such as a parent repository that contains the app folder. If `RepositoryPath` is omitted, Git operations fall back to `RootPath` and resolve the Git work-tree top-level from there.

### Project + workspace registry (ADR-0042)

In parallel with the legacy `<projectKey>` slug layout above, projects also
live as records in `<TaskRepository>/.metadata/projects.json` with immutable
identifiers (`PROJ-001`, `PROJ-002`, ...) and a workspace membership in
`<TaskRepository>/.metadata/workspaces.json`. Legacy `WatchPaths` entries are
auto-registered only when `projects.json` did not exist at the registry's first
load. An existing empty file is authoritative and does not trigger seeding.
The id is monotonic and never re-used; the display name can change without
breaking task keys derived from the id. `StorageLocation` is immutable during
ordinary project editing and changes only through a controlled legacy-store
migration.

An existing `projects.json` that cannot be deserialized aborts startup with the
`project-registry-load-failed` classification. The backend must not substitute
an empty registry or persist over the invalid file. Before any registry write
that reduces the project count, the current file is copied byte-for-byte to
`projects.json.quarantine-<UTC timestamp>` and the
`project-registry-shrink-quarantined` classification records both counts and
paths.

Per-project task counters move out of the sidecar `.task-counter.json` and onto the project record (`NextTaskKeySeq`). Display-keys like `ATP-130` are formatted as `<ShortCode>-<seq>` (e.g. `ATP` for the historic "Agent Task Processor" / `ASS` for the historic "Agent Software Studio" short code + sequence `130`; existing short codes are not auto-renamed by the agent-orchestrator rebrand because they are persisted on every existing card).

Projects created through `POST /api/projects` use `<TaskRepository>/projects/PROJ-NNN/tasks/` as their watched task-store root. The product repository remains a separate `RepositoryPath` and never receives a new `.orchestrator/jobs` store. Legacy projects can retain their existing storage location until a controlled migration.

The full layout migration (jobs sharded under `projects/PROJ-XXX/jobs/<bucket>/<slug>/`, jobKey moved to `PROJ-NNN::<slug>`) is tracked under F45c and is not active yet; jobs continue to live in the lane-folder layout shown below until that ships. New code that needs to address a project should prefer the registry id over the watch-path string.

Project test runs are independent, commit-bound evidence objects stored in
`<TaskRepository>/.metadata/test-runs/<PROJ-NNN>.json`. The schema-versioned
file contains the run lifecycle and never stores card assignments. Task reads
derive their best run, commit distance, direction, and diff containment from
Git ancestry, so moving or completing a card cannot rewrite test evidence.
Normal writers use `POST /api/projects/{project}/test-runs` and
`PUT /api/projects/{project}/test-runs/{runId}`.

`<TaskRepository>/.metadata/migrations/superseded-commits-v1.json` is the
durable report and completion marker for the one-time historical delivery-round
repair. It lists both marked commits and ambiguous cards left untouched. The
backend writes it atomically after scanning delivered and archived cards; an
unreadable existing report fails that repair rather than silently rerunning it.

`<TaskRepository>/.metadata/migrations/historical-integration-verification-v2.json`
is the current durable count report and completion marker for accepted and
archived cards that predate acceptance integration recording. V2 extends the
scan to every terminal acceptance-start card that the acute alert can inspect.
It reads folders directly, preserves records written by V1, and contains counts
for all six verification classes. Task rows remain limited to
`content-on-fence` and `genuinely-missing`. The V1 report remains as historical
evidence and is never overwritten. Related `task.json.integrationRecords[]`
rows are append-only and use stable versioned ids, so an interrupted or repeated
sweep cannot duplicate card bookkeeping.

## Operational Boundary

The filesystem layout is the storage contract, not the normal operating interface. Agents and scripts must create, move, delete, and reorder jobs through the API:

- `GET /api/watch-paths`
- `POST /api/tasks`
- `POST /api/tasks/{jobId}/move?watchPath=...`
- `POST /api/tasks/reorder`
- `DELETE /api/tasks/{jobId}?watchPath=...`
- `DELETE /api/tasks/orphan-folder` with body `{"watchPath":"...","lane":"7-archive","folder":"..."}` for scanner-invisible residue folders in terminal lanes only. This path refuses folders that contain `job.json` and logs `task-orphan-folder-deleted` or `task-orphan-folder-delete-failed`.

Direct folder edits are reserved for backend implementation, migrations, recovery, and tests that deliberately exercise this contract. Normal queue management goes through the API so validation, owner identity, SignalR updates, and the Task Access layer remain authoritative.

### Project docs tree

A watched project's own `docs/` tree is the Wiki source of truth. Folders are
categories, Markdown and self-contained HTML files are pages, and the UI renders
that physical tree directly. There is no app-owned organisation manifest and no
virtual grouping layer.

Wiki mutations are real filesystem operations committed through the backend:
create page, create folder, move / rename, and delete. Per-file history stays
meaningful because the file path shown in the Wiki is the path in Git. The
companion `/wiki/history/{relPath}` provenance endpoint and the tree API are in
[wiki-tree.md](./wiki-tree.md).

## Job Folder Layout

Each visible state is a folder, and each job is a subfolder inside one state:

```text
0-backlog/
1-preparation/
2-ready/
3-progress/
4-auto-review/
5-human-review/
5e-escalated/
6-completed/
7-archive/
```

`0-backlog` is the triage staging area: it is the default landing lane for new jobs created via `POST /api/tasks` without an explicit `targetState`. Auto-pickup never reaches into the backlog; promoting a job to `1-preparation` or `2-ready` is an explicit user action.

The review lanes are explicit (ADR-0025): `4-auto-review` is the durable compatibility key for the visible Post Processing lane, `5-human-review` is the lane for operator approval of accepted work, and `5e-escalated` is the lane for operator decisions the orchestrator could not resolve. The pre-ADR-0025 numbered lanes (`4-review`, `5-completed`, `6-archive`) are migrated automatically on backend boot.

Each job folder uses this structure:

```
<job-name>/
  job.json          # Metadata owned by the application
  prompt.md         # Task description for the CLI agent
  enrichment-report.json
                    # Optional: latest pre-spawn prompt-enrichment audit
  status.md         # Generated review protocol
  lifecycle.json    # Optional: richer phase history (intake / post-processing checks)
  completion-acceptance.json
                    # Optional: structured requirements, evidence, blockers, and completion lifecycle
  post-processing-outcomes.jsonl
                    # Optional: typed Post Processing outcomes
  .metadata/        # Application-owned sidecars (pipeline-execution.json, files.json, ...)
                    # Optional: .metadata/prompts.jsonl (raw step-call prompts)
                    # Optional: .metadata/spawned-tasks.jsonl (task-spawner dedup ledger, AGT-2028)
  attachments/      # Input files supplied with the task
  intake/
    enriched-context.md
                    # Exact labelled context appended to the launch prompt
  results/          # Output evidence such as screenshots
                    # Optional: results/review-evidence.jsonl (audit / review findings)
  logs/             # CLI output, including the capped logs/cli-output.log
    session-events.jsonl
                    # One row per confirmed local start or remote claim; new terminal rows add result, finishedAt, status, exitCode, and durationSeconds
    timeline.jsonl  # Unified derived lifecycle ledger
    cli-output.log.1
                    # Optional previous 10 MiB tail; ignored by workspace Git
```

All backend writers route durable CLI text through `CliOutputLogFile`. The
active `cli-output.log` and its single `.1` rotation are each limited to 10 MiB.
Rotation is line-aware and leaves a visible `[cli-output-rotated]` marker in the
active file. At startup, `CliOutputLogMaintenanceService` applies the same cap
to oversized legacy logs and adds `**/logs/cli-output.log.1` to the central task
repository's `.gitignore`. The workspace evidence committer independently
excludes that rotation path, including when a legacy rotation was once tracked.

## Template Files

### job.json

```json
{
  "id": "<job-name>",
  "title": "<description>",
  "createdAt": "<ISO-8601>",
  "state": "0-backlog",
  "order": 1,
  "agent": "codex",
  "cliType": "codex",
  "taskType": "chore",
  "tags": ["architecture"]
}
```

**States:** `0-backlog` -> `1-preparation` -> `2-ready` -> `3-progress` -> `4-auto-review` -> `5-human-review` / `5e-escalated` -> `6-completed` -> `7-archive`

**Optional fields:**

- `agent` - CLI ownership marker. New tasks must use a supported CLI value (`claude`, `codex`, `copilot`, `gemini`) and should keep `agent` aligned with `cliType`. Do not use `agent: "human"` as a parking mechanism; park visible non-running work by choosing a lane such as `0-backlog` or `5-human-review`. On creation, the API materializes `agent`, `cliType`, and `model` from the owner client's defaults when not explicitly provided, so new jobs carry concrete values rather than the old misleading `agent: "human" / cliType: null / model: null` triple. When a request explicitly selects `cliType`, an omitted or incompatible `model` is normalized to that CLI's catalog default; later `cliType` and `model` mutations apply the same compatibility rule so persisted tasks do not mix a CLI with another vendor's model. See `AgentTypes` in `src/AgentTaskboard.Shared/Models/CliTypes.cs`.
- `taskType` - structural classification, one of `bug`, `feature`, or `chore` (default for legacy and technical work). Drives the small chip rendered on the kanban card and the type filter pill in the header. Legacy `user-story` values on disk are silently normalised to `feature` on read; no bulk re-write is performed.
- `tags` - string array of workspace tag ids. The label and colour for each id come from `<workspace>/tags.json` served by `GET /api/tags`. The registry seeds seven default tags on first read (`ui-ux`, `performance`, `quality`, `architecture`, `security`, `docs`, `observability`), each carrying a `description` field that surfaces in tooltips and the filter dropdown. On boot, missing seed ids are merged into an existing registry by id; user-customised rows are never overwritten. Unknown ids on a job (registry entries that were soft-deleted) render as a faint ghost chip on the card.
- `references` - structured cross-references by stable key (`{ "dependsOn": ["CAR-3"], "relatedTo": [], "blockedBy": [], "supersedes": [], "workbenches": ["CAR-W2"] }`), written through `PUT /api/tasks/{id}/references`. Task keys are project-unique via the project short code, so they resolve across projects. Document references use the owning project's `SHORT-W<number>` namespace; unknown document keys are rejected, while descriptor `relatedTaskKeys` remain a readable legacy bridge. **`dependsOn` is the "waits-on" relation (AGT-2029) and is scheduler-load-bearing**: a `2-ready` card whose `dependsOn` targets have not all reached `6-completed`/`7-archive` is held back from auto-pickup (it stays visibly "waiting" on the board, never a silent skip) and becomes pickable once they are fulfilled. A dependency can opt into an additional content-release boundary with the object form `{ "key": "CAR-3", "releaseGate": true }`; legacy string entries remain valid and unchanged. A release-gated dependency is fulfilled only when the target is terminal and carries `"released": true`. Fulfillment resolution is cross-project and archive-inclusive. A `dependsOn` cycle is a configuration error: it is reported (structured `waits-on-cycle` log + an error chip on the card) and the card is skipped so the runner never deadlocks. Writing an unknown task key is allowed (a warning, not a hard failure) because the referenced task may be created later. The board card renders a state-aware, clickable dependency chip and distinguishes waiting for completion from waiting for release. The task detail shows both directions (waits-on / blocked-by-me) plus linked document key chips. Only `references` is persisted; the task read endpoints (`GET /api/tasks`, `/api/tasks/grouped`, `/api/tasks/{id}`) additionally fold on a derived, non-persisted `waitsOn` object - `{ "items": [{ "key", "resolved", "fulfilled", "releaseGate", "targetReleased", "waitingForRelease", "targetJobId", "targetTitle", "targetState", "targetWatchPath" }], "blocked", "cycleDetected" }` - computed per request from `dependsOn` against an archive-inclusive, whole-workspace index (`WaitsOnEvaluator`). It is absent/null on cards without `dependsOn` edges and drives the chip; the frontend never recomputes fulfillment client-side. `GET /api/projects/{projectName}/workbenches/{key}/references` derives the reverse card list from the same reference index.
- `released` - explicit content approval for release-gated dependents. It defaults to `false` when absent and is written through `PUT /api/tasks/{id}/release` with `{ "released": true|false }`. Terminal lane movement never sets it implicitly; an operator or a dedicated release step must do so. The operator path is the detail-view References section, which offers the decision on the target card and inline on each waiting dependent (AGT-2709); every write appends one `task_released` timeline event carrying the deciding actor and the release-gated dependents it moves.
- `commits[].supersededBySha` - exact replacement object created by a conflict-free platform rebase. The original commit remains as readable history but is excluded from integration completeness, while a new `commits[]` entry carries the replacement SHA and inherits the original producer attribution. This is independent from `supersededByAttempt`, which records a later authored delivery generation.
- `integrationRecords` - append-only application bookkeeping for historical
  integration verification. Each row carries a stable id, one of
  `integrated-verified`, `integrated-historical`, `no-code-expected`,
  `no-attribution-legacy`, `content-on-fence`, or `genuinely-missing`, the
  integration branch, and the Git or artifact evidence used. Agents and
  ordinary task mutations never replace these rows. The historical startup
  sweep and the guarded `POST /api/tasks/{address}/integration-records`
  operator-verification endpoint are the only writers. The address may be a
  stable task key or the folder name within the selected project. A duplicated
  legacy `id` returns `409` instead of selecting one card. The endpoint rejects
  in-flight lanes and is idempotent by record id.

The application owns transitions between these states. Successful CLI runs move from `3-progress` to `4-auto-review`, whose visible label is Post Processing; the orchestrator's review pass then either reissues (back to `3-progress`), accepts-as-done (forward to `5-human-review`), or escalates (forward to `5e-escalated` with a `[supervisor]` chat-note and an escalation verdict). The user always confirms accepted work before it moves from `5-human-review` to `6-completed`. Failed or stopped runs stay in `3-progress` for inspection, restart, or continuation.

**Optional substate:** `job.json` may also carry a `"phase"` string that distinguishes orchestrator-driven substates within the same folder-level state. The hybrid V1 model (see `docs/concepts/expanded-lifecycle-lanes-plan-2026-05.md`) keeps the seven folder-level states above as the durable skeleton and uses `phase` plus the optional sidecar `lifecycle.json` for the Intake / Post Processing lanes the kanban projects on top.

| State          | Allowed phase values                                                                                                                |
|----------------|-------------------------------------------------------------------------------------------------------------------------------------|
| `2-ready`      | `human-ready` (default), `intake-running`, `intake-blocked`                                                                          |
| `3-progress`   | `execution-running`, `execution-stalled`, `post-processing-running`, `post-processing-blocked`, `awaiting-review`                    |
| `4-auto-review`| `post-processing-running` (default), `post-processing-blocked`, `awaiting-review`                                                     |
| other states   | none (the state already says enough; the field should be absent or `null`)                                                          |

`phase` is **application-owned and optional**. Existing job folders that predate the field continue to render in the default lane of their state without any rewrite: `2-ready` falls back to `human-ready`, `3-progress` with a running CLI falls back to `execution-running`, `3-progress` with a generating summary falls back to `post-processing-running`, and stopped or failed runs in `3-progress` keep rendering as the execution lane. Unknown or out-of-state values are dropped on read with a warning. Boot-time scans never rewrite a phase-less `job.json` to add a default; lazy defaulting happens in code.

### lifecycle.json (optional)

```json
{
  "version": 1,
  "phase": "intake-running",
  "phaseEnteredAt": "2026-05-06T10:12:00Z",
  "blockingReason": null,
  "intakeChecks": [
    { "name": "duplicate-detection", "status": "passed", "startedAt": "...", "finishedAt": "..." },
    { "name": "clarity-probe",       "status": "running" }
  ],
  "postProcessingChecks": []
}
```

Optional sidecar carrying the richer phase history that does not fit on the wire-level `phase` field: which intake or post-processing checks were scheduled, when the current phase was entered, and the last blocking reason. Absent on legacy job folders; the wire-level `phase` field is the source of truth. The follow-up tasks `ready-orchestrator-intake-lane` and `post-processing-orchestrator-lane` populate this file.

Post Processing attempt boundaries actively rewrite this sidecar. A confirmed
new coding process sets `execution-running` with the process start timestamp and
clears checks from the preceding review attempt. Entering Post Processing, or a
startup recovery sweep that re-enqueues it, sets `post-processing-running`,
replaces older checks, and timestamps the new attempt. Leaving Auto Review or
terminating the worker changes every active `pending` or `running` check to
`completed`, `failed`, or `skipped` and supplies `finishedAt`; an active check
must never survive a terminal boundary. `skipped` is the honest terminal for a
pass that closed without running its check, for example when the decision
engine passed the card over because another actor owns it: `completed` would
claim a decision that never happened, and a check left `running` is re-armed as
a phantom in-flight step by every backend restart and never terminalizes. Automation scans exclude cards whose `task.json`
has `fixture: true`, while explicit fixture management and test APIs may still
read them.

### .metadata/review-attempt.json and results/history/ (optional)

An explicit operator move out of `5-human-review` or `5e-escalated` into a
work/review lane opens a new review-attempt epoch:

```json
{
  "epoch": 1,
  "startedAt": "2026-07-23T08:30:00Z",
  "actor": "human:operator@example.com",
  "reason": "Infrastructure repaired; reassess from fresh evidence.",
  "fromState": "5e-escalated",
  "toState": "4-auto-review",
  "cliLogLineBoundary": 842
}
```

Legacy tasks without this file are in epoch 0. Automatic moves never change it.
The CLI-log boundary keeps the append-only historical prefix readable while
excluding it from decisions in the new epoch.
Before fresh Post Processing starts, active verdict residue is moved under
`results/history/review-epoch-NNNN/operator-requeue-<timestamp>/`. These files
remain available for audit but are not active deliverables and are excluded from
review prompt inventories. `GET /api/tasks/{id}/runs` projects the authoritative
current epoch plus the operator-requeue timeline boundaries as
`reviewAttemptEpoch` and newest-first `reviewAttemptCycles`; the Task Detail Runs
modal renders that projection beside the CLI run history.

### parked-blocker.json (optional)

Written whenever a task enters `5-human-review` or `5e-escalated`, and deleted when it leaves. It records what the parked card is waiting for in a form a sweep can re-check, next to the freetext park reason it preserves verbatim.

```json
{
  "version": 1,
  "blockerType": "review-subject-unmaterialisierbar",
  "condition": {
    "kind": "git-ancestor",
    "parameters": {},
    "description": "The card branch carries the current integration branch, so a review baseline can be materialized again."
  },
  "lane": "5-human-review",
  "parkedAt": "2026-07-29T22:07:00Z",
  "reason": "4x ReviewInfra/BaselineUnavailable - parked for an operator decision, no auto rerun",
  "lastEvaluation": { "status": "blocked", "at": "2026-08-03T12:00:00Z", "detail": "'task/agt-2220' still does not contain 'develop'." },
  "reportedRecallableAt": null
}
```

`blockerType` is the escalation category, or `operator-decision` for a manual park. `condition.kind` is one of `manual` or `git-ancestor`; `lastEvaluation.status` is one of `blocked`, `recallable`, or `undeterminable`. The recall sweep owns `lastEvaluation` and `reportedRecallableAt`; `TaskInfo.ParkedBlocker` projects the file at read time and adds the lane age. Legacy parks without the file are backfilled from `enteredLaneAt`. A `recallable` blocker is reported, never auto-requeued. See [parked-card recall](../../concepts/parked-card-recall.md).

### post-processing-outcomes.jsonl (optional)

Append-only JSON-Lines file holding orchestrator-owned Post Processing outcomes. This file records what happened between the coding CLI finishing and the task reaching Human Review; it does not authorize source edits or lane moves by the supporting identity.

```jsonl
{"version":1,"at":"2026-06-09T12:34:00Z","jobId":"feature-task","project":"agent-taskboard","outcome":"pass-to-human-review","performer":"supporting-agent","performerCliType":"claude","stepId":"orchestrator-decision","summary":"All aspects passed.","evidenceRef":"aspect-tests-and-evidence.md","findingRefs":[],"followUpTaskIds":[]}
{"version":1,"at":"2026-06-09T12:35:00Z","jobId":"feature-task","project":"agent-taskboard","outcome":"findings-added","performer":"tool","stepId":"security-analysis","summary":"One non-blocking finding was recorded.","evidenceRef":"results/review-evidence.jsonl","findingRefs":["finding-1"],"followUpTaskIds":[]}
```

Valid `outcome` values are `pass-to-human-review`, `findings-added`, `needs-follow-up-task`, `needs-human-input`, and `failed-post-processing`. Valid `performer` values are `orchestrator`, `supporting-agent`, and `tool`. `performerCliType` is optional and should be one of the supported CLI values when a supporting CLI performed the check.

### completion-acceptance.json (optional)

The completion gate writes this structured sidecar before aspect review. It preserves the complete requirement source plus every evidence item and explicit blocker with its source and reason. Its lifecycle object keeps four separate facts: `turnComplete`, `implementationComplete`, `taskAccepted`, and `deploymentPushPending`. A successful `TASK_DONE`/process terminal can therefore complete implementation while acceptance is still under review and platform-owned commit, push, or deployment remains pending.

The gate does not derive open work from `status.md` bullets or narrative. Only structured terminal/process evidence and explicit `TASK_BLOCKED` or `TASK_NEEDS_INPUT` terminals drive the pre-review completion ruling. The structured aspect verdict updates `taskAccepted`; deployment/push pending never masquerades as incomplete implementation.

### results/review-evidence.jsonl (optional)

Append-only JSON-Lines file holding **task-level review evidence**: findings produced by security audits, code-review passes, task checks, or notes written by a reviewer. Lives next to the screenshots in `results/` so it travels with the job folder and stays out of the app source repository.

```jsonl
{"id":"e1","source":"security-audit","severity":"high","title":"Token logged in plaintext","body":"`AuthService.LogIn` writes the bearer token to `logs/cli-output.log`.","createdAt":"2026-05-08T12:34:00Z","runIndex":2,"fileRefs":["backend/Services/AuthService.cs:142"],"artifacts":["results/playwright/auth-spec/screenshot.png"]}
{"id":"e2","source":"code-review","severity":"warn","ruleId":"QS-NG-002","title":"Angular quality rule matched","body":"See the QS-owned rule library for the rule statement and remediation guidance.","createdAt":"2026-05-08T12:34:11Z","fileRefs":["frontend/src/app/example.component.scss:18"],"artifacts":["results/quality-analysis/post-analysis-angular-rules.json"]}
{"id":"e3","source":"human-note","severity":"info","title":"Visual regression spotted","body":"Compose box border looks 1px off when the steer pill is active.","createdAt":"2026-05-08T12:36:00Z","artifacts":["results/screenshots/compose-steer.png"]}
```

Schema, per line:

| Field            | Type                        | Required | Notes |
|------------------|-----------------------------|----------|-------|
| `id`             | string                      | yes      | Stable identifier; producers may use a uuid or short slug. |
| `source`         | string                      | yes      | One of `security-audit`, `code-review`, `task-check`, `human-note`, `other`. Unknown values fall back to `other` on read. |
| `severity`       | string                      | yes      | One of `info`, `warn`, `high`. Unknown values fall back to `info`. |
| `title`          | string                      | yes      | Single-line headline rendered in the panel. |
| `ruleId`         | string                      | no       | Stable producer-owned rule identifier such as `QS-NG-002`; rendered separately from the finding title. |
| `body`           | string                      | no       | Free-form Markdown (kept short — the panel does not virtualize). |
| `createdAt`      | ISO-8601 string             | yes      | UTC. |
| `runIndex`       | integer                     | no       | The 1-based run index this finding belongs to (matches `runs[].index` from `/api/tasks/{id}/runs`). |
| `artifacts`      | string array                | no       | Paths relative to the job folder, e.g. `results/foo.png` or `results/playwright/spec/file.png`. |
| `fileRefs`       | string array                | no       | Repository-relative file references, optionally `path:line`. Treated as opaque text. |
| `acknowledged`   | boolean                     | no       | True when a reviewer has marked the finding as seen. Latest entry per `id` wins (see "Mutating an existing finding" below). |
| `followupJobId`  | string                      | no       | Set by the "Create follow-up task" action; references the queued task id in the same workspace. |

Hard rules:

- **The endpoint and the UI must never break on a malformed line.** Skip non-parseable JSON and missing required fields with a warning; surface the rest.
- **No state-machine effects from the file.** `JobTransitionService` does not consult this file. A producing pipeline step may independently use its in-memory result for a steered retry while appending the same findings here for review. Security findings are the explicit exception to quality gating in the current policy: they remain recorded and visible but never block or steer the pipeline.
- **Mutating an existing finding** (acknowledging it, attaching a follow-up id) is done by appending a new line with the same `id` and the updated fields. Readers fold the file into latest-per-id; the file stays append-only.
- **Storage location.** Inside the job folder, never inside `agent-taskboard-dev/` itself. Meta-level documentation (decisions, ADRs, doctrine) goes in source; task-level evidence stays beside the job.

### results/decision.json or results/decision.html (optional)

The active operator hand-off for a task in `5e-escalated`. A conforming
`decision.json` uses `decision-surface/v1`: one question, one to eight options,
an optional recommendation, consequences, optional free steering configuration,
and an allowlisted existing `steer` or `move` action. A conforming
`decision.html` embeds the same object in
`<script type="application/json" data-agent-studio-decision>` and may add a
self-contained visual explanation.

Task Detail renders HTML with an opaque-origin sandbox and renders every action
in trusted host chrome. A selection reaches the existing Continue/Steer or Move
endpoint. Steer text is retained in Activity; Move sends the selection as the
lane-change `reason`. There is no separate decision persistence.

The complete ownership, schema, lifecycle, precedence, allowlist, and failure
contract is [Operator Decision Surface](../../operations/decision-surface/README.md).

### model-qualification.jsonl (optional)

Append-only benchmark foundation for the `pre-model-qualification` decision and
the matching CORE result. It lives at the job root. The decision captures the
task profile, live catalogue source, recommendation, effective selection,
override source, and estimated saving. The outcome captures the actual model,
reasoning level, tokens, run status/verdict, and pipeline attempt.

```jsonl
{"at":"2026-07-11T18:00:00Z","event":"decision","decisionId":"...","jobId":"ui-polish","project":"agent-taskboard","cliType":"codex","taskType":"chore","surface":"frontend polish","complexity":"small","score":-2,"recommendedModel":"<catalogue economy rung>","recommendedThinkingLevel":"low","selectedModel":"<catalogue economy rung>","selectedThinkingLevel":"low","selectionSource":"qualification","estimatedSavingsPercent":65,"reason":"chore/small/frontend polish; ...","catalogueSource":"cli-pty"}
{"at":"2026-07-11T18:02:00Z","event":"outcome","jobId":"ui-polish","project":"agent-taskboard","model":"<actual model>","thinkingLevel":"low","status":"completed","verdict":"success","inputTokens":1200,"outputTokens":300,"cacheReadTokens":500,"cacheCreationTokens":0,"attempt":1}
```

Hard rules:

- `event` is `decision` or `outcome`; consumers join rows by `jobId` and attempt chronology.
- Model ids and reasoning levels are values from the live CLI catalogue, never a second Studio-owned list.
- `selectionSource=task-override` means the recommendation was reporting-only and the card pin won.
- Logging is best-effort observability and never changes the run or lane decision.

### .metadata/prompts.jsonl (optional)

Append-only JSON-Lines file holding the **raw final prompt of every one-shot step-call** dispatched for the task (review aspects, the code-review-grade pass, and any other step routed through the central one-shot CLI seam). It closes the "Rohdaten komplett" gap: these step prompts are rendered at run time and otherwise land in no durable file at the task, unlike the main run / follow-up prompts which already live in `prompt.md` and the chat. The capture is written at central dispatch *before* the CLI call (so a prompt survives a later timeout / failure), is best-effort (an IO failure is logged and swallowed, never propagated into the run), and is keyed to the pipeline step so the task-detail Overview can show the exact prompt a step sent. Main-run prompts and follow-ups are deliberately **not** written here — recording them again would be double bookkeeping.

```jsonl
{"at":"2026-06-10T10:00:00Z","stepId":"aspect-requirement-fit","templateRef":"review-aspect-requirement-fit.md","model":"claude-haiku-4-5","cli":"claude","source":"review-aspect","prompt":"Assess the requirement fit of this change.\n..."}
{"at":"2026-06-10T10:01:12Z","stepId":"post-code-review-grade","templateRef":"code-review-grade.md","model":"claude-opus-4-8","cli":"claude","source":"review-decision","prompt":"Grade this change A/B/C/D.\n..."}
```

Schema, per line:

| Field         | Type            | Required | Notes |
|---------------|-----------------|----------|-------|
| `at`          | ISO-8601 string | yes      | UTC dispatch time (when the prompt was sent to the CLI). |
| `stepId`      | string          | yes      | Pipeline step id, e.g. `aspect-requirement-fit` or `post-code-review-grade`. The read-model keys off this to show the prompt next to the matching step. |
| `templateRef` | string          | no       | Runtime prompt template the final text was rendered from. Null when the prompt is built inline. |
| `model`       | string          | yes      | Model the prompt was sent to. |
| `cli`         | string          | yes      | CLI the prompt was sent through (lowercase, e.g. `claude`). |
| `source`      | string          | no       | Usage-attribution source tag (e.g. `review-decision`) when the call site supplied one. |
| `prompt`      | string          | yes      | The final, raw prompt text exactly as piped to the CLI. Literal newlines are JSON-escaped so each record stays a single line. |

Hard rules:

- **The endpoint and the UI must never break on a malformed line.** Skip blank and non-parseable JSON lines with a warning; surface the rest (mirrors `results/review-evidence.jsonl`).
- **No state-machine effects.** This is raw provenance, not a gate. No transition consults it.
- **Read model only.** The task-detail Overview parses this file via `GET /api/tasks/{id}/step-prompts`; it never duplicates the derivation into another stored file ("Rohdaten komplett, Herleitung als Lesemodell").

### prompt.md

```markdown
# Job Prompt

Describe what the coding agent should build or change.

## Goal
Build feature X.

## Acceptance Criteria
- Criterion 1
- Criterion 2

## Constraints
- Work on the selected task only.
- Put review evidence under the job folder's results/ directory when needed.
```

`prompt.md` remains the operator-authored source and is never overwritten by
preprocessing. Before a fresh CLI dispatch, `PromptEnrichmentService` writes the
exact additive block to `intake/enriched-context.md`, then atomically writes
`enrichment-report.json` as the dispatch commit marker. The report records the
status (`enriched`, `unchanged`, `fallback-unenriched`, or `blocked`), original
and enriched hashes, policy/catalog identity, detected candidates and every
selection decision, exact appended blocks with source/revision/digest, token
attribution, nullable cost estimates, timing, warnings, and errors. If the
report cannot be persisted, the CLI must not spawn. The project can disable the
step through its `pre-prompt-enrichment` pipeline-step override; the resulting
`unchanged` decision is still reported.

### status.md

`status.md` is application-owned. It is normally generated from
`logs/cli-output.log` after a local run or from durable V1 run events and
deliverables in the Task Server. Result generation is an awaited post-core
gate with bounded summary-only retry. Exhaustion records a typed `Degraded`
Result and leaves the completed core run reviewable. If the file is still
missing at a move into
`4-auto-review`, `5-human-review`, `5e-escalated`, or `6-completed`,
`TaskTransitionService` creates an honest marked scaffold before the move and
refuses the transition if that write fails. Startup also backfills missing
Results in `5-human-review`, `6-completed`, and `7-archive`. Agents may read the
file for recovery context, but durable evidence should live in logs or
`results/`.

```markdown
# Status

- Result: Success
- Duration: 4 min

## What Was Done
- Implemented the requested change.
```

## Quick Start: Create A New Task

Create the task through the UI or `POST /api/tasks`. The application creates
the lane and task folders below the project's central `StorageLocation` and
writes `job.json` and `prompt.md`. Do not create those folders manually and do
not add `.orchestrator/jobs` to the product checkout. See the
[Task API skill](../../../.agents/skills/task-api/SKILL.md) for the current request
shape and client-identity requirements.
