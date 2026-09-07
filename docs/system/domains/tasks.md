# Tasks Domain Map

Version: 2026-08-27
Status: System-of-record map for task storage, lanes, and API mutation changes.

Use this when a change touches job folders, lane states, task metadata,
workspace registry records, task CRUD, ordering, review evidence, run timeline,
or commit attribution.

## Execution modes

- `coding` is the default source-mutating mode.
- `planning` and `research` are report-only modes. They run the lightweight
  report pipeline without git, build, test, Stylelint, aspect, or code-quality
  steps and must finish with a clean product checkout.
- Research is recognized by canonical task metadata `mode=research`, shown as a
  Research pill, and reinforced by the effective prompt heading `Research:`.
  Its primary artifact is `results/report.html`; every optional companion is
  linked from that HTML. See the
  [Research task delivery convention](../../operations/research-deliverables/index.html).
- `concept` is document-first. It uses an isolated worktree but may change only
  one `docs/<slug>/` dossier. The repository dossier is canonical; a copy under
  task `results/` is optional. `workbench.json` names the source card in
  `sourceTaskKeys` and remains `status=decision-pending` until sight review.
  The published document, not `status.md`, is the promotion source.
- New concept Dossiers use the embedded Article document v2 template at
  `docs/app/templates/article-document-v2.html`. It keeps the house-style rules
  for calm noun headings, factual copy, bounded reading width, theme variables,
  and inline SVG diagrams, and includes a bounded append-only Implementation
  section. The concept prompt retains
  `docs/operations/haertung-verteilte-ausfuehrung/index.html` only as a
  compatibility fallback when the canonical template is absent.
- Claims about visible product surfaces require full-bleed screenshot evidence.
  Each figure states the fact it proves, dates and labels the provenance of
  every capture, uses no more than one accented annotation per finding, and
  includes both themes when the finding depends on theme, contrast, status, or
  visual hierarchy. Published operations-Dossier assets live at
  `docs/operations/<slug>/assets/`; a new `docs/<slug>/` concept delivery uses
  its adjacent `assets/` directory. The prompt points authors to
  `docs/operations/setup/presentation-capture.md`,
  `frontend/e2e/visual-evidence/presentation-capture.spec.ts`,
  `scripts/stable-frontend-boot-probe.mjs`, and
  `frontend/e2e/fixtures/dev-backend.ts` for the capture and readiness patterns.
- `references.workbenches` is the canonical typed card-to-Dossier edge.
  `TaskInfo.ConceptDossier` still detects a repository-relative
  `docs/.../index.html` path from
  `results/deliverables.md` first and `status.md` second. Task detail renders
  that one path only as a compatibility fallback. A concept in `5-human-review` or
  `6-completed` with neither a link nor a deliberate no-dossier explanation
  gets one compact `No dossier linked` notice with the two correction actions.
  Existing cards are not bulk-migrated. MKT-21 is the reference example and
  remains a manual backfill rather than an application migration.
- The Dossier-key reference reuses the same detail row and operator actions,
  not a second Dossier block. The Markdown detector remains read-only
  compatibility for already-linked cards and never produces a duplicate visible
  reference.
- A delivered concept waits in `5-human-review` with a
  `concept-sight-review` marker. This is a successful delivery state, including
  when the agent reports `NEEDS_INPUT`; it is not an escalation.
- Sight-review acceptance moves the concept to `6-completed`. The concept
  promotion endpoint creates idempotent coding cards in `1-preparation` and
  relates them to the source concept. Each `implementationTasks` entry is one
  independently reviewable slice and becomes one card with a bounded
  `acceptanceScope`. Promotion rejects an open-ended entry such as `implement
  all recommendations`; use one entry per slice or an Epic with slice children.

## Entry Points

- [docs/system/contracts/filesystem.md](../contracts/filesystem.md) defines the durable
  job-folder layout, lane catalog, and state strings.
- [docs/system/contracts/agent-task.md](../contracts/agent-task.md) defines what the app
  owns and what the CLI owns inside a task.
- [docs/system/contracts/protocol-style.md](../contracts/protocol-style.md) covers `status.md`, Activity Log
  markers, `attachments/`, `results/`, and image retention.
- [docs/system/architecture/runner-lanes/progress-lane-writers.md](../architecture/runner-lanes/progress-lane-writers.md)
  lists every writer for the `3-progress` lane.
- [docs/app/schemas/task-mutation-request.schema.json](../schemas/task-mutation-request.schema.json)
  and [docs/app/schemas/task-find-result.schema.json](../schemas/task-find-result.schema.json)
  pin task API shapes.

## Project onboarding contract

Project onboarding is one product workflow, not a configurable project-source
catalogue. The onboarding UI has no source-type selector and Workspace Settings
has no Project Sources administration page. A repository URL records the
project's repository location; it does not promise a managed clone or a cloud
workspace workflow.

`POST /api/projects` is the product onboarding mutation. It accepts project
identity plus optional `repositoryPath`, `repositoryUrl`, and
`executionRunner`, creates the central
`<TaskRepository>/projects/PROJ-NNN/tasks/` store, and activates registry-backed
scanning and watching without editing `WatchPaths` or restarting the backend.
The repository URL is stored as the well-known `repo` project URL. New projects
never place task data in the product checkout; legacy in-repository stores stay
in place until an explicit migration.

The same basic values remain editable after creation in Project Settings.
`PUT /api/projects/{PROJ-NNN}` is the canonical update mutation for display
name, short code, workspace, colour, repository checkout, working directory,
repository URL, default CLI/model, and execution runner. The request uses
optional patch fields plus explicit `clear*` flags for optional values. Registry
fields are validated together before they are persisted. The runner value is
delegated to `ProjectSettingsService`; it is not duplicated in the project
registry. The Project Settings UI continues to edit runner assignment through
the dedicated `PUT /api/projects/{projectName}/execution-runner` contract.
Project id, source type, storage location, creation time, and the task and
document key counters are immutable and are never accepted as update fields.

The project registry is fail-closed. If an existing
`<TaskRepository>/.metadata/projects.json` is not deserializable, startup
terminates with `ProjectRegistryLoadException` and
`project-registry-load-failed`; no empty replacement state is exposed or
written. Legacy `WatchPaths` seeding is eligible only when the registry file
was absent at its initial load. Every persisted project-count reduction first
copies the current file to `projects.json.quarantine-<UTC timestamp>` and logs
`project-registry-shrink-quarantined`.
During that one-time seed, a later WatchPaths entry whose display name matches
an already discovered project is skipped and logged as
`registry-bootstrap-watchpath-name-collision`. This prevents two storage paths
with the same operator-facing name from creating a second, unconfigured ghost
project.

## API-First Task Organization

Agents must organize tasks through the application API, never by direct
filesystem mutation under `agent-taskboard-workspace/projects/**` or
`agent-taskboard-workspace/.metadata/**`.

- Required skill for scripted board mutation:
  [.agents/skills/task-api/SKILL.md](../../../.agents/skills/task-api/SKILL.md).
- The skill covers `watchPath`, `X-Client-Id`, lane names, creation, move,
  reorder, archive, and triage templates.
- The route is `/api/tasks` (the former `/api/jobs` alias was removed, ADR-0058).
  How the API identifies a project (raw `watchPath` today, `shortCode`/`projectId`
  target) is documented in
  [../concepts/api-project-identity-and-watchpath.md](../../concepts/api-project-identity-and-watchpath.md).
- If an operation is missing from the API, create a follow-up task instead of
  reaching around the API.
- Multi-task lane moves use the asynchronous batch contract. Send up to 500
  entries to `POST /api/tasks/batch-move`, retain the returned job `id`, and
  poll `GET /api/tasks/batch-move/{id}` for `completed`, `succeeded`, `failed`,
  and ordered per-item results. The coordinator processes each card as an
  independent bounded transition on one background worker. It never holds a
  lock for the lifetime of the batch, and an item failure does not stop later
  items. Completed job snapshots also expose batch-local lane-lock, scanner,
  and Git timings for operational diagnosis.
- Accepted integration commits that already exist in the project repository
  can be appended through
  `POST /api/tasks/{id}/commits/integration?watchPath=...` with
  `{ "sha": "<full-40-character-sha>" }`. The commit message must name the
  task key. The operation appends or refreshes that SHA in `commits[]`, mirrors
  it as the final singular `commit`, and never creates or rewrites Git history.
- Operator- or GPT-reviewed historical classifications can be appended through
  `POST /api/tasks/{address}/integration-records?watchPath=...`. The address may
  be a stable task key or the task folder name within the selected project. A
  duplicated legacy `id` returns `409` rather than selecting an arbitrary card.
  The request uses the six classes owned by
  `HistoricalIntegrationVerificationSweep`, a stable caller-supplied record id,
  and explicit evidence. The server owns
  `recordedAtUtc`, rejects non-schema classes, and refuses cards in Preparation,
  Ready, Progress, or Post Processing so an in-flight delivery cannot be sealed
  by bookkeeping. Repeating a record id is an idempotent no-op. This mutation
  never changes a lane, commit chain, task branch, or Git history.
  Historical verification records also remove the card from the live
  accepted-integration alert: they describe a reconciled historical fact, not
  an active acceptance transaction.
- Integration status accepts persisted abbreviated Git SHAs of at least seven
  hexadecimal characters when they match a reachable full SHA by prefix.
  Zero-file lifecycle entries whose subjects start with
  `wip(runner): salvage before teardown` or `chore: snapshot for review` remain
  visible attribution metadata but are not delivery expectations. A matching
  subject with changed files remains a real integration expectation. Target
  branch ancestry is stronger evidence than stale file metadata: a commit that
  is already reachable from the integration branch is never discarded by the
  zero-file marker heuristic.
- Every `commits[]` write re-derives `filesChanged` and `files` from the Git
  object when it is reachable, regardless of whether the producer is local
  attribution, remote salvage, a delivery backfill, or an operator/recovery
  path. Startup performs an idempotent, Git-read-only repair of missing commit
  metadata on non-archived cards and writes only the affected `task.json` files
  through `TaskMutationService`.
- Remote commit attribution is evaluated independently for every fenced run
  attempt. Each attempt's verified delivery range passes through the
  foreign-task guard before persistence. Card-level `commits[]` is the
  SHA-deduplicated union across generations; a later attempt does not replace
  earlier valid commits, and inherited SHAs retain their original
  `runAttemptId`, `runnerId`, delivery `branch`, and proving `resultSha`.
- A fenced remote ResultEnvelope attributes the exact `BaseSha..ResultSha`
  range. It must not recompute that boundary from the live integration branch:
  release tasks can publish `main` before completion, making the live merge
  base equal to the result and incorrectly producing an empty range. Review
  subjects retain `BaseSha` so recovery uses the same immutable boundary.
- Requeueing an integration-conflict or integration-error delivery marks that
  generation's entries with `supersededByAttempt`. The entries remain in
  `commits[]` for audit history, while attribution, aggregate diffs, provenance,
  and integration status use only entries without that marker. The temporary
  value `next-attempt` is replaced by the next fenced `runAttemptId` when its
  remote attribution lands.
- A platform-owned mechanical rebase retains each original `commits[]` entry,
  marks it with `supersededBySha`, and appends its replacement object with the
  same producer attribution. Both SHA and attempt supersession remove a
  historical entry from integration completeness while preserving its audit
  chain. If this attribution write fails, the integration merge is rolled back
  before any push is released.
- Startup runs the one-time `superseded-commits-v1` healing sweep over delivered
  and archived cards. It marks only a missing runner salvage fence with a later
  integrated commit from a proven different generation, at least 90 percent
  changed-file overlap, no more than three omitted paths, and comparable total
  breadth. Cases outside those bounds remain untouched and are listed in the
  durable sweep report for manual review.
- Startup also runs the bounded `remote-completion-attribution-v1` repair over
  recent delivered and archived remote subjects. It accepts only a verified
  immutable result ref plus an exact subject or provenance base, passes the
  resulting non-empty range through the normal foreign-task guard, and writes
  through `TaskMutationService`. The same pass materializes missing remote
  token receipts from that attempt's CLI-log window. Ambiguous cards remain
  unchanged and are listed in the durable migration report.
- Accepted-card `integration.status` is a read-time projection of attributed
  commit membership in the configured target branch, cached against that
  branch's current HEAD. Lane state, provenance merge records, pipeline success,
  and curated merge subjects do not force `integrated`; an out-of-band merge is
  detected on the next read.
- Human acceptance is transactional. A coding card remains in
  `5-human-review` with phase `integrating` until the delivery reaches `Merged`,
  `MergedAfterRebase`, or `AlreadyMerged`. `NoTaskBranch`, conflict, gate failure,
  and error return it
  to ordinary Human Review with an Integration failed badge and timeline
  evidence. Before the already-integrated decision, acceptance fetches the
  configured origin integration ref and evaluates refreshed local plus remote
  ancestry. A remote-only out-of-band integration therefore skips the merge
  queue and gate, while local/remote divergence becomes a failed integration
  record instead of being overwritten. The `integrationpending` tag is an
  internal recovery marker, not a second UI status.
- A current integration failure is projected as typed card state from the
  durable merge step. `integration.failure` carries a stable code, concise
  label, operator-facing reason, and whether focused rebase recovery applies.
  This distinguishes a merge conflict, a delivery that needs rebasing, a build
  gate failure, unavailable task-key validation, an invalid review subject,
  a missing task branch, and a generic integration error without changing the
  legacy top-level `conflict-skipped` compatibility status.
- `AcceptanceRailHostedService` is the platform-owned mover for routine
  post-integration progress. By default it runs immediately at backend startup
  and every 180 seconds. It accepts a coding card from `5-human-review` only
  when the same Git-derived projection says `integrated`; concept and other
  no-code cards remain for human review. A recoverable `conflict-skipped` card
  in `5-human-review` or `5e-escalated` receives the shared rebase steer and is
  promoted to the top of `2-ready`. After five rail requeues it stays escalated
  with an explicit exhaustion reason. The `orchestrator-hold` tag and entries
  in `AcceptanceRail:HoldList` match task id, key, or tag and suppress every
  automatic action. `AcceptanceRail:Enabled`, `IntervalSeconds`, and
  `MaxRequeues` configure the bounded loop. Each action is written to
  `timeline.jsonl`; the structured run log and
  `GET /api/pipeline/acceptance-rail` expose lane depth, action counts, failure
  count, and the last-run timestamp. Session orchestrators may observe these
  facts but do not own the lane mutation.
- The immutable current review subject selects the authoritative delivery
  generation for integration membership. When a reissue rebases an accepted
  commit to a replacement object id, target-branch ancestry of that reviewed
  result proves integration; attributed SHAs from superseded review epochs
  remain history and do not force a permanent `partial` card state.
- A move may set `operatorOverride: true` only when targeting `6-completed`.
  This is an explicit, one-shot operator waiver, never a default. Pipeline
  history and `status.md` retain the override and reason. Concept and other
  no-branch cards are exempt through their mode, kind, `taskType=concept|decision`,
  or the explicit `noBranchExpected: true` card field.
- `HistoricalIntegrationVerificationSweep` runs once off the startup request
  path before the accepted integration inventory. The V2 pass reads every task
  folder directly, groups Git reads by repository, and processes card writes in
  bounded batches. It preserves every V1 row and extends the original
  missing-record population to terminal acceptance-start cards inspected by the
  acute alert. Each uncovered card receives one append-only
  `integrationRecords[]` row:
  `integrated-verified`, `integrated-historical`, `no-code-expected`,
  `no-attribution-legacy`, `content-on-fence`, or `genuinely-missing`.
  `no-attribution-legacy` is reserved for pre-recording cards whose task JSON
  has no `commits[]` attribution field and whose branch, result, and salvage
  evidence cannot prove delivery. Target-branch ancestry uses the
  same abbreviated-SHA and superseded-generation rules as the card projection;
  report-only classification requires both a no-code expectation and a task
  result artifact. The durable migration report contains aggregate counts and
  lists only the two operator-facing classes. These rows are bookkeeping only:
  the accepted integration recovery loop starts after the sweep and excludes
  every card carrying one. `AcceptedIntegrationInventorySweep` then lists only
  `content-on-fence`, `genuinely-missing`, or current recorded Error and
  NoTaskBranch outcomes. The acute alert still detects a new acceptance stall
  after the one-time pass.

Task creation can carry a structured `routing` request with the observed
surface, affected component, and navigation project. `ComponentRoutingService`
resolves that request against versioned ownership mappings stored on project
registry records. A confident cross-project match replaces the requested
destination, validates the ticket prefix, and appends consumer integration and
deployment acceptance criteria. Unknown, conflicting, or low-confidence
ownership returns `409` with a routing question instead of silently using the
navigation project.

Cross-project `change-project` is also a re-key operation. The state machine
reserves a destination-project key, stages source and destination folders under
hidden names, promotes the complete destination, and rolls back on failure.
This prevents the AGT-2166 archived-orphan/lost-task failure mode.

## Related Concepts

- [../concepts/completion-review-and-remote-runner-stability.html#provenance](../../concepts/completion-review-and-remote-runner-stability.html#provenance):
  why runner assignment is scheduling policy rather than historical fact, how a
  Task retains an ordered multi-runner route, and when a host change continues,
  blocks, or starts a new attempt.
- [../concepts/task-integration-and-merge-workflow.md](../../concepts/task-integration-and-merge-workflow.md):
  how a finished task's branch reaches `develop` (worktree, deferred merge, the
  transactional Human Review accept trigger).
- [../concepts/task-integration-merge-config-analysis.html](../../concepts/task-integration-merge-config-analysis.html):
  why integration semantics should not depend on `maxParallelism`.
- [../concepts/auto-review-evidence-gate-analysis.html](../../concepts/auto-review-evidence-gate-analysis.html):
  why auto-review reissues good work ("Needs rework") and the evidence-gate fix.

## Card test evidence projection

The board's test-evidence block is a read-time projection, not one persisted
test status on `task.json`. `TestRunService` reconciles these evidence classes:

| Evidence class | Durable source | Reaches the card | Matching rule |
|---|---|---|---|
| Project Test Quality run | `<TaskRepository>/.metadata/test-runs/<projectId>.json` | Yes | Exact task commit, a later run that contains the task commit, or a qualifying integration recut found through Git ancestry |
| Remote Review `build-tests` grade | `remote-review-grade-<attemptId>.md` in the task folder | Yes | The grade must contain a `build-tests` verdict and its immutable result SHA must equal or contain the current card anchor |
| Post-processing build/test gate | `post-steps/build-test-gate-*.log` | Yes | The tested SHA must equal or contain the current card anchor |
| Pre-develop build gate and pre-main test gate | `post-steps/pre-develop-build-gate-*.log` and `pre-main-test-gate-*.log` | Yes | The tested merge SHA must equal or contain the current card anchor |
| Review aspect `tests-and-evidence` | `aspect-tests-and-evidence.json` or Markdown twin | No green claim | This is an LLM review verdict, not proof that deterministic commands passed |
| Build profile readiness | Project settings `buildProfile.status` | No | It controls pickup readiness. Evidence exists only after configured commands execute in a gate or recorded project test run |
| Agent-authored test output and Playwright screenshots | `status.md`, run logs, and `results/` | No automatic green claim | They remain inspectable task artifacts until a structured SHA-bound producer records them |

A project test run is assigned only when the project Test Quality API has a
recorded run and Git ancestry links its commit to the card. Gate execution does
not create a project test-run record. Gate logs and Remote Review reports are
separate task-owned evidence sources and are reconciled alongside project runs.
A source for an older card commit is deliberately ignored after the card gains
a newer commit.

The 2026-07-29 archived-card incident demonstrated the former gap:

| Card | Former card projection | Evidence that actually existed |
|---|---|---|
| AGT-2416 | `Evidence pending: No test run assigned` | Remote Review `review_05aa90204763466abc2627c9be2eedc8`: `Pass`, `build-tests: pass`, exact SHA `3aa5ad85` |
| AGT-2399 | `Evidence pending: No test run assigned` | Remote Review `review_b916bab377404c1f9457f6cf075c58f1`: `Pass`, `build-tests: pass`, exact SHA `67d3039c` |
| AGT-2426 | `Evidence pending: No test run assigned` | Remote Review `review_8017590a9dd34619b1480e0fdbb5938e`: `Pass`, `build-tests: pass`, exact SHA `d1649ce9` |

All three reports also recorded an immutable materialized HEAD equal to the
card commit. The old projection queried only the project Test Quality store,
which had no runs for these cards, and never inspected their task-owned report
files. The corrected card copy names the source and SHA, for example
`Review build-tests Pass at d1649ce9` or
`Build/test gate green at <sha>`. Truly unassigned cards use
`No test evidence assigned` and say that no SHA-linked project run, build gate,
or review grade is recorded. Only a matched planned or running project test
run uses `Evidence pending`. A build/test gate log with verdict
`NotApplicable` and reason `no verify commands derivable` projects the neutral
`No build/test defined` state. A true `Skipped` gate remains `not-proven` and
uses the red attention treatment, so the card cannot imply that an applicable
gate passed without executing.

## Files-tab document projection

`GET /api/tasks/{id}/artifacts` projects supported top-level task documents into
the Files tab: Markdown, self-contained `.html` / `.htm`, and structured
`aspect-*.json`. `status.md` remains owned by Result, and subfolders remain out
of scope. A file opens on its current content without historical controls. Its
History tab lists prior snapshots compactly by run, date, and grade; choosing a
row loads that snapshot. Arbitrary From/To comparison between review snapshots
is intentionally absent because it did not support an operator workflow. The
history list remains the basis if a concrete comparison need emerges. HTML
content is fetched through the existing task-file endpoint and
rendered through `srcdoc` with `sandbox="allow-scripts"`. The deliberate omission
of `allow-same-origin` keeps an opaque origin, so interactive artifacts cannot
read Studio cookies, storage, DOM, or APIs. Artifacts that require same-origin
or controlled network integration belong to the Dossier viewer described in
[Experimentier-Dossier](../../concepts/experimentier-workbench.md#5-viewer-interactive-html-and-project-previews).

Task-authored links are resolved separately from the top-level Files manifest.
Every task Markdown surface binds relative `results/*` and allowed `logs/*`
links, plus absolute runner paths containing those folders, to the open card's
task context. `GET /api/tasks/{id}/results/{**path}` serves nested result
artifacts with traversal containment and type-aware responses. Self-contained
HTML opens inline in a new tab under a response-level sandbox policy; known
preview types keep their MIME type, and unknown binary types remain
`application/octet-stream`. Allowed text log links use the existing
workspace-scoped task file route.

## Parked-card blocker and recall

Every move into `5-human-review` or `5e-escalated` writes a machine-readable
`parked-blocker.json` next to `task.json`: the blocker type (the escalation
category, or `operator-decision` for a manual park), a condition a sweep can
re-check, the park timestamp, and the original freetext reason verbatim. The
write happens in `TaskStateMachine.RecordParkedBlocker`, at the same lane-change
choke point that appends the `lane_changed` ledger row, so every park path gets a
marker; leaving a parked lane clears it. That ledger row also names why the lane
changed: `details.cause` (one of `LaneChangeCauses`, supplied by the transition
site or derived from the lane pair for a human actor) and `details.causeDetail`
(a short qualifier); see the lane-transition section of the
[cycle-time stage model](../../concepts/cycle-time-stage-model.md#lane-transitions).

`ParkedCardRecallSweep` re-evaluates those conditions on a timer and through
`GET /api/parked-cards`. A card whose precondition is provably gone is REPORTED -
one `parked_blocker_resolved` timeline row plus a `recallable` status on
`TaskInfo.ParkedBlocker` - and is never re-queued: "no auto rerun" stays the
decision of whoever parked the card, and re-queueing remains the operator lane
move that opens a fresh review-attempt epoch. A condition no probe can decide is
reported as `undeterminable` rather than optimistically as resolved.

`TaskInfo.ParkedBlocker` is a read-time projection of that marker, never
persisted on `task.json`. It carries the lane age (`parkedForSeconds`), so a card
that has sat unlooked-at for days is visible as such. Cards parked before the
marker existed are backfilled from `enteredLaneAt` by the sweep. Full rationale
and the AGT-2220 incident: [parked-card recall](../../concepts/parked-card-recall.md).

## Result transition invariant

`TaskTransitionService` is the single enforcement point for Result availability.
Every successful move into `4-auto-review`, `5-human-review`, `5e-escalated`,
or `6-completed` carries a non-empty `status.md`. When no generated protocol is
available, the service writes a marked, evidence-only scaffold before the
folder move and enriches its own scaffold after the move with the computed
integration projection. It never replaces a real Result. Backend startup runs
the same idempotent repair over missing Results in `5-human-review`,
`6-completed`, and `7-archive`; repaired files are marked as operator
backfills.

## Task-tab refinement projection

The task-detail inspector orders its tabs as `Task | Activity | Result`.
`Task` renders the current `prompt.md`, followed by a quiet chronological
refinement history. `GET /api/tasks/{id}/runs` derives that history at read
time from existing evidence:

- `prompt-N.md` supplies full multiline operator refinements created through
  Extend mode.
- The run timeline supplies other operator follow-ups from the `[user]` rows
  in `logs/cli-output.log`; the paired run start is the displayed time and the
  session-event reason supplies the reason when present.
- `orchestrator-follow-up-history/*.md` supplies system reissues, including
  their recorded timestamp, reason or cause, and verbatim steering prompt.

The projection de-duplicates an Extend prompt when the nearby run-log
follow-up contains the same normalized text. It adds no task files and no
write-side contract. Legacy follow-ups that exist only as `[user]` log rows
therefore remain visible without inventing a second persistence mechanism.

## Runs projection

`GET /api/tasks/{id}/runs` is built by `TaskReader`; `task.json.runs[]` is not
its store. `TaskReader` loads `logs/session-events.jsonl`, bounded
`logs/cli-output.log`, `logs/timeline.jsonl`, and the task's RunAttempts. The
session event identifies one invocation and now carries an additive terminal
display receipt for new runs. Appending a successor start first closes the
previous open row as `superseded`, so one task never has more than one open Run
record. Read-time fallback first uses terminal Attempt Authority,
`agent_run_finished`, or CLI-exit evidence. If none exists, a successor start
closes the orphaned predecessor as `superseded`; bounded CLI activity is the
last duration-only fallback.
Rows with no terminal evidence expose `closeoutSource=legacy-missing`, which the
Task Detail Runs panel renders as `Not recorded (legacy run)`.

The Runs aggregate duration can fall back to the persisted CORE agent step in
the task-folder `pipeline-execution.json` when no row has a duration. That aggregate
fallback explains historical cases where the header knew the total while an
individual row did not; it is not an alternative per-run store.

The Task Detail panel lifts a common CLI/model/thinking-level value into the
panel summary and renders only per-run deviations. The primary row keeps the
full run number, trigger, result, and duration visible.

## Project proposals

The Project Hub proposal queue is backed by dated Markdown generations under
`docs/concepts/proposals/<YYYY-MM-DD>/`. Structured frontmatter carries the finding,
evidence screenshot, proposed change, estimated effort, severity, durable
decision status (`proposed`, `approved`, `rejected`, or `spawned`), and the
spawned task key. `GET /api/projects/{projectName}/proposals` lists the complete
history. `POST .../{id}/decision` records rejection or creates one coding card
through `TaskMutationService` and then records its key as `spawnedTask`.

New generations use `scripts/generate-project-proposals.mjs`. The generator is
idempotent: an existing proposal document is preserved so a repeated survey run
cannot erase an operator decision.

## Epic lifecycle

- Epics are `kind=epic` task records; membership is the child's `epicId`.
- `GET /api/epics` is archive-inclusive. A finished epic remains queryable when
  all of its children are in `6-completed` or `7-archive`.
- The Epic overview separates active and completed rollups, shows `x / y done`,
  and expands to child lane status plus project identity.
- Manual Epic creation requires a title and goal description. If a planning
  run parses no sub-tasks, the runner records the failed decomposition and
  returns the Epic to `0-backlog` instead of leaving an empty completion.
- A remotely assigned Ready Epic is claimable for planning even though local
  auto-pickup still skips Epic containers. Local and remote completion both use
  `EpicDecompositionLifecycle`, `EpicDecompositionParser`, and
  `EpicSubTaskFactory`. Valid plans create child coding cards with the Epic's
  project, `epicId`, CLI, and model defaults, append
  `.metadata/spawned-tasks.jsonl` planning-spawn evidence, and move the Epic to
  `5-human-review`. Empty, invalid, or source-mutating plans record a failed
  decomposition and return the Epic to `0-backlog`.
- A planning run is source-read-only and therefore never carries a Result-SHA,
  so no ReviewAttempt can be minted for it. `4-auto-review` is consequently not
  a legal planning-completion lane: an Epic parked there waits forever on a code
  review of a subject that does not exist, and the report-only exception in the
  decision engine does not apply because the card is `mode=coding`. The lane is
  a pure decision in `EpicRunPolicy.PlanningCompletionLane`.
- Two Human Review behaviours follow from an Epic owning no delivery branch.
  Accepting it to `6-completed` skips the transactional merge, which would
  otherwise return `NoTaskBranch` and bounce the card back on every accept. The
  boot-time verdict-less backfill also leaves it alone: an Epic is verdict-less
  by construction, not by legacy, so escalating it would only move the dead end
  one lane over.
- Empty Epic cleanup uses the Task API to move records to `7-archive`; it never
  deletes the task folder. Archived zero-member cleanup records are omitted
  from the overview, while completed Epics with historical children remain.

## Key Code

- `task-server/TaskServerStore.cs` and `TaskServerEndpoints.cs`: separated
  control-plane task, run, lease, event, artifact, audit, and canonical replay
  store. The path-free
  `GET /api/v1/projects/{projectId}/tasks/{taskIdentity}/history` projection is
  the reconnect source for detached Studio clients. It also carries the latest
  typed Result-finalization state (`Retryable`, `Ready`, or `Degraded`) and its
  bounded attempt count, so card reviewability does not depend on inferring
  success from the presence of a scaffold.
- `backend/Endpoints/Tasks/*`: task CRUD, runner, files, git, review evidence,
  merge, pipeline, and query endpoints.
- `backend/Services/TaskAccess/*`: typed read/list/mutate/transition/subscribe
  boundary for task state.
- `backend/Services/Tasks/TaskScannerService.cs`: task scan and projection.
- `backend/Services/Tasks/TaskMutationService.cs`: metadata edits.
- `backend/Services/Tasks/TaskTransitionService.cs`: lane transition owner and
  side effects.
- `backend/Services/Tasks/TaskStateMachine.cs`: durable lane state handling.
- `backend/Services/Tasks/TaskJsonFile.cs`: `job.json` persistence.
- `backend/Services/Tasks/LaneMutexRegistry.cs`: per-project lane serialization.
- `backend/Services/Tasks/CommitAttributionService.cs` and
  `CommitAttributionRunner.cs`: deterministic commit-to-task binding.
- `backend/Features/TestRuns/TestRunService.cs` and
  `TaskScopedTestEvidenceReader.cs`: project-wide test-run lifecycle plus the
  read-time card evidence projection derived from the latest task-owned commit,
  Git ancestry, Remote Review build-tests grades, and deterministic gate logs.
- `backend/Services/Tasks/ReviewEvidenceLog.cs` and
  `ScreenshotIndexService.cs`: review evidence and visual proof.
- `backend/Features/Registry/WorkspaceSettingsService.cs` and
  `WorkspaceSettingsEndpoints.cs`: per-workspace default orchestrator settings
  (`WorkspaceSettings` beside `WorkspaceRecord`, persisted to
  `.metadata/workspace-settings.json`), exposed via
  `GET /api/workspaces/{id}/settings`, `PUT .../orchestrator-model`, and
  `PUT .../autonomy` (404 on unknown id). This is the workspace tier of the
  two-tier orchestrator config; the precedence and read sites live runner-side
  (ADR-0061, see [runner.md](runner.md)).

## Invariants

- The durable lane sequence is
  `1-preparation -> 2-ready -> 3-progress -> 4-auto-review -> 5-human-review -> 6-completed -> 7-archive`.
- Task creation defaults to `0-backlog` only when `targetState` is absent.
  Every explicit valid `targetState` is authoritative, including review and
  terminal lanes used by operator or automation workflows. Invalid states fail
  creation instead of silently selecting another lane.
- `job.json.acceptanceScope` is the card-owned requirement-fit boundary. A
  `bounded-slice` scope requires one slice name and one or more concrete
  criteria. Reviewers may not demand a parent Dossier or broader wishlist
  outside that boundary. Missing scope means full-task acceptance. See
  [workflow-sized task cutting](../../operations/workflow-sized-task-cutting.md).
- `4-auto-review` remains the disk/API key even when the UI labels it Post
  Processing.
- `5-human-review` is where the user gets the final say. The orchestrator does
  not move a task directly from auto-review to completed.
- A canonical Remote Review report does not perform a lane move. Infrastructure
  outcomes retry the same immutable ReviewSubject. A non-infrastructure report
  remains in `4-auto-review` through successful workspace cleanup, which
  atomically creates a full-envelope Task Server orchestration run. Fenced
  Engine settlement then moves the task to `2-ready`, `5-human-review`, or
  `5e-escalated`. The bounded reissue count spans decision runs and coding
  attempts for the task; it does not reset with Studio or Engine restart.
  Consecutive Remote Review decisions with the same blocking aspect,
  classification, and summary escalate with that reason after two rounds by
  default, even when the broader reissue budget remains.
- Integration remains an explicit operator decision after Human Review. The
  Remote control plane may durably accept and schedule that command, but Post
  Processing does not infer acceptance or move directly to `6-completed`.
- Moving a task from `6-completed` to `7-archive` in task detail requires a
  second confirmation while `integration.status` is anything other than
  `integrated`. This is an operator warning, not a server-side hard block.
- Only `2-ready` and `3-progress` tasks can be started. A `2-ready` card is
  additionally held back from auto-pickup while its `references.dependsOn`
  ("waits-on") targets are unfulfilled (AGT-2029); see the waits-on gate in
  [runner.md](./runner.md) and the `references` field in
  [../contracts/filesystem.md](../contracts/filesystem.md).
  An edge may use `{ "key": "AGT-2050", "releaseGate": true }` to require an
  explicit target-card release after terminal completion. Operators and
  dedicated release steps set that independent flag through
  `PUT /api/tasks/{id}/release`; ordinary string edges keep their existing
  terminal-state-only semantics.
- Rendered task references resolve through `POST /api/tasks/reference-status`.
  Send `{ "keys": ["AGT-2050", "CAR-2"] }`; keys are trimmed, uppercased,
  deduplicated, and capped at 200. The response is `{ "items": [...] }`, where
  each item contains `key`, `exists`, `taskKey`, `title`, `lane`, project
  identity/colour, merge reachability and branch names, and `reviewGrade`.
  Known-project deleted keys return a ghost (`exists: false`); keys whose
  shortcode is absent from the project registry are omitted. Consumers must
  batch page/message references rather than issue one request per reference.
  The task scan and merge reachability inputs are cached, merge membership is
  computed once for the batch, and the frontend hydrator also caches resolved
  keys for its lifetime. The reusable consumer contract and CAC-3 chat host
  wiring are documented in [frontend/AGENTS.md](../../../frontend/AGENTS.md#task-reference-microcards).
- `references.workbenches` stores project-scoped document keys such as
  `AGT-W4`. The task write validator resolves these keys against the owning
  project's canonical descriptor catalogue and rejects an unknown key.
  Discovery assigns missing keys from the project's independent
  `NextWorkbenchKeySeq` counter and atomically writes the complete
  `workbench.json`; existing keys survive folder and slug renames. The reverse
  query `GET /api/projects/{projectName}/workbenches/{key}/references` returns
  every referencing card. Descriptor `relatedTaskKeys` remain a separately
  labelled legacy bridge and do not replace the derived keyed edges.
- A delivering card resolved through `references.workbenches` or a descriptor
  `sourceTaskKeys` back edge receives the Dossier maintenance prompt contract.
  It appends only its own dated implementation entry between the canonical log
  markers. The review gate rejects rewrites outside that log or changes to prior
  entries and records the outcome as `post-dossier-maintenance` in the task's
  pipeline timeline. The same source and typed references feed the existing
  documented-lifecycle policy, so all terminal references produce the quiet
  `Ready to document` proposal.
- Successful CLI runs move from `3-progress` to `4-auto-review` through
  application code. Failed or stopped runs remain inspectable.
- Direct filesystem access by app code is restricted to the bounded service
  layer and covered by architecture tests.
- The combined test-evidence projection is never persisted on `task.json`.
  Project runs remain project-scoped objects; task-owned Remote Review grades
  and gate logs remain immutable files in the task folder. A successful source
  proves a card only when its commit equals the card commit or contains its
  change. Direct ancestry proves ordinary commits; a reachable curated
  `merge(KEY)` or `merge-recut(KEY)` integration anchor proves rewritten task
  commits only when that integration postdates the card's current attributed commit.
  Missing commit timestamps disable this fallback rather than reusing historical
  key-only evidence. Planned and running matches are pending evidence; an older
  green run remains visible as `diff not included` and never turns the card green.
- `GET /api/tasks` and `GET /api/tasks/grouped` never run a Git process on the
  request path. Merge, integration, publish, and test-evidence fields come from
  the latest completed in-memory `TaskListGitProjectionCache` snapshot. A cold
  read may omit those additive fields while it queues one background refresh;
  later reads fold in the completed snapshot. Input changes and a two-second
  refresh interval queue a new single-flight refresh without making the request
  wait. `GitProcessTelemetry` records `tasks/list` and `tasks/grouped` separately
  from `tasks/list-refresh`, so request rollups must remain at zero spawns even
  when HEAD churn causes the background refresh to recompute Git projections.

## Project Git inventory contract

- `GET /api/git/inventory` and `GET /api/git/history` return an in-memory
  snapshot and never start Git on the request path. `GitInventoryRefreshHostedService`
  warms every configured project at startup. A cold read returns an explicit
  warming shape until that first snapshot completes.
- Invalidation uses filesystem metadata for `.git/packed-refs`, `.git/refs`,
  selected local and protected refs, and `HEAD`. An unchanged signature is a
  cache hit. A changed signature queues one recomputation, and concurrent reads
  coalesce while continuing to receive the previous snapshot. Every completed
  inventory includes `computedAt`.
- Inventory Git reads are bounded to local heads, tags, and the integration and
  release remote-tracking lines. The history projection is computed on the
  same background refresh, limited to 400 commits, and decorated from that same
  bounded ref set. It never uses `git log --all`.
- Result, quarantine, and salvage delivery refs come from task review-subject
  and attempt indexes. They are not discovered by walking
  `refs/remotes/origin/*`. Gate materialization fetches the named subject and
  then its immutable SHA only; it never installs the wildcard origin refspec.
- The daily retention pass removes local
  `refs/remotes/origin/agent-studio/results/*` and
  `refs/remotes/origin/agent-studio/quarantine/*` copies only when an archived
  card indexes the exact ref. This sweep uses `git update-ref -d` locally and
  never deletes or prunes a ref on origin.
- `git-info` inventory telemetry records `refCount`, `computedAt`, and the cache
  decision `hit`, `recompute`, or `coalesced`. Request telemetry records
  `spawns=0`; subprocess timings belong to `git/inventory-refresh`.

## Execution location on task reads

`GET /api/tasks`, `GET /api/tasks/grouped`, task detail, and SignalR task
updates carry `TaskInfo.executionLocation`. Consumers must use its actual
`runnerId`, `executionKind`, lease state, and heartbeat for live attribution.
The project `executionRunner` setting appears only as `configuredRunnerId` for
comparison and queued-remote display. Cards in `3-progress`, the detail header,
and Overview share the same projection, so concurrent cards each retain their
own lease owner rather than inheriting a project-wide runner status.

Settled session events preserve the same projection as historical run evidence.
Historical entries never reuse disconnected warning treatment. See the
[execution location schema](../schemas/task-execution-location.schema.json).

For a Ready card offered to a remote Runner, `executionLocation.lastRejection`
contains the latest refusal for the current lane stay. It names the Runner and
preserves the admission reason, including missing `repositoryUrl`, capability
mismatch, failed project delivery preflight, and dispatch preparation failure.
The board and task detail render the complete reason inline. The durable field
is removed after dispatch succeeds; lane-entry timestamps prevent an older
refusal from leaking into a later Ready generation.

## Outcome issue presentation contract

Task reads derive `TaskInfo.outcomeIssue` from typed runner markers in
`logs/cli-output.log`. `summary` remains a bounded compatibility value for
compact consumers. `technicalDetails` carries the complete normalized source
line and is restricted to explicit technical-details surfaces. User-facing
failure cards map the issue `kind` to a complete human sentence; they never use
`summary` or `technicalDetails` as primary copy. Unknown kinds use a generic
failure sentence while retaining the full diagnostic under the disclosure.

## Verification

- API and mutation changes need endpoint tests plus task-access or service tests
  that prove optimistic concurrency, lane state, and side effects.
- Any new direct filesystem path construction must pass the architecture
  isolation test and justify why it belongs inside the bounded layer.
- Visual-evidence, status, or protocol changes need fixtures that prove old and
  new task folders still render.
