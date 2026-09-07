# Pipeline Domain Map

Version: 2026-08-27
Status: System-of-record map for task-processing pipeline changes.

Use this when a change touches pre/core/post steps, pipeline catalog entries,
step ordering, step history, step cost, review fan-out, or the task-detail
pipeline view.

## Entry Points

- [docs/system/architecture/decisions/proposed/adr-0051-task-processing-pipeline.md](../architecture/decisions/proposed/adr-0051-task-processing-pipeline.md)
  is the concept ADR for CI/CD-style task pipelines.
- [docs/concepts/distributed-agent-studio-target-architecture.md](../../concepts/distributed-agent-studio-target-architecture.md)
  covers the Server/Runner split, stream logs, leases, and shared state.
- [Runner provenance, host handoff, and continuation](../../concepts/completion-review-and-remote-runner-stability.html#provenance)
  defines how a pipeline cycle, agent run, execution attempt, and step execution
  retain ordered runner/host placement across planned review handoffs and
  recovery. A single task- or pipeline-level runner field is not sufficient.
- [docs/app/schemas/pipeline-definition.schema.json](../schemas/pipeline-definition.schema.json)
  pins versioned pipeline definitions.
- [docs/app/schemas/step-run.schema.json](../schemas/step-run.schema.json) pins
  per-step telemetry rows.
- [docs/system/domains/token-pricing.md](./token-pricing.md) is the single source for pipeline
  cost derivation.
- [Workflow arguments become unbounded fan-out](../../operations/common-problems/workflow-args-json-string-fanout/)
  records the serialized-argument failure mode and the validation and resource
  caps required before parallel work starts.

## Key Code

- [Model Routing Policy](./model-routing-policy.md) is the canonical model and
  thinking-level selection policy, including weighted criteria, correctness
  floors, benchmark confidence, quota handling, and reissue promotion.
- `backend/Features/Pipeline/ModelQualificationService.cs`: zero-token PRE-step
  that classifies the task in project context and maps it onto the selected
  CLI's live model/reasoning ladders. `IModelEconomyAdvisor` is the stable
  `TokenEconomy.SuggestModel` seam.
- `backend/Features/Runner/PromptEnrichmentService.cs`: deterministic,
  zero-selector-token `pre-prompt-enrichment` step. It classifies the authored
  card, selects curated versioned project/style/delegation blocks, appends at
  most two optional blocks within a 1,500-token budget, and persists
  `enrichment-report.json` before dispatch. Failure to persist the report blocks
  dispatch. The step is default-on and can be disabled through the normal
  per-project `PipelineSteps` convention.
- `backend/Features/Pipeline/PipelineStepEconomyAdvisor.cs`: opt-in automated
  recommendation layer for cheap pipeline work. It passes only live-discovered
  Spark candidates to `IModelEconomyAdvisor`, preserves explicit step pins, and
  falls back to the normal runtime model when no qualified Spark model exists.
- `backend/Features/Pipeline/PipelineCatalogue.cs`: standard, report-only,
  concept, and UI pipeline definitions, step ids, default ordering, step run
  modes, and display names.
- `backend/Features/Pipeline/QualityAnalysis/`: the Quality Studio in-process
  package adapter, repository-owned activation policy, canonical finding
  projection, and the first executable Angular named-rule pass.

## Quality Studio analysis steps

Quality Studio analysis is a standard pipeline category (`StepKind.Analysis`),
not an optional generic tool invocation. The standard catalogue names seven
separate post-core steps: Angular rules, C# rules, model review, visual quality,
security, redundancy, and consistency. The Angular rule pass is the first
executable slice; the other axes remain explicit catalogue slots until their QS
package sensors land.

The runtime consumes the `AgentOrchestrator.CodeQuality` analysis core as an
in-process DLL. It calls `QualityAnalysisCore` and receives canonical Quality
Studio findings; there is no HTTP fallback. Package publication and rule content
remain owned by Quality Studio. Agent Studio references QS rule ids such as
`QS-NG-002` and `QS-NG-003` and does not copy their statements or check logic.

Default activation is conventional and derives from the changed paths of the
completed card:

| Card class | Default analysis steps |
|---|---|
| Frontend-touching Angular | Angular named-rule pass and visual quality |
| Backend .NET | C# named-rule pass and security |
| Mixed | Union of the frontend and backend defaults |

Projects override step activation only through the versioned repository file
`.quality/agent-studio.json`, validated by
`docs/app/schemas/quality-analysis-policy.schema.json`. Per-card fields, central
project settings, appsettings, and environment variables are not policy inputs.
QS rule enablement and severity remain in QS-owned `.quality/rules.json`.

Each completed pass writes `results/quality-analysis/<step-id>.json`, appends
findings with their `ruleId` to `results/review-evidence.jsonl`, and records the
artifact on `pipeline-execution.json`. Medium-or-higher findings from implemented
quality axes feed the existing bounded steered-retry loop. In accordance with
QS-90, unfixed security findings are documented and visible but do not block or
steer the pipeline in this policy version.
- `backend/Features/Pipeline/ConceptWorkbenchContract.cs`,
  `ConceptWorkbenchPublisher.cs`, and `ConceptPromotionService.cs`: the
  document-first concept contract. One isolated concept run may author exactly
  one `docs/<slug>/` dossier, publishes it through the managed project-artifact
  commit boundary, verifies the source-card key plus `decision-pending` status
  and task-file links, waits for human sight review, then creates coding cards
  from the descriptor.
- `backend/Features/Docs/DossierMaintenanceService.cs` and
  `DossierImplementationContract.cs`: resolve a delivery card's Dossiers from
  `references.workbenches` or descriptor `sourceTaskKeys`, frame the CORE run,
  and prove at review that one dated card entry was appended inside the
  canonical implementation log without changing decision content.
- `backend/Features/Pipeline/PipelineCatalogue.cs`,
  `backend/Features/Runner/UiTaskPipelineRouter.cs`, and
  `backend/Features/Runner/UiIterationGate.cs`, plus
  `backend/Features/Runner/VisualQa/`: the named UI iteration pipeline, shared
  EvidenceGate-based routing, mandatory iteration result layout,
  stable-equivalent Playwright capture, multimodal visible-defect verdict, one
  durable automatic steer, and bounded hand-off to Human Review. The durable
  Part 2 consumer shape is defined in
  [the UI task pipeline contract](../contracts/ui-task-pipeline.md).
- `backend/Services/Pipeline/PipelineExecutionLog.cs`: per-run
  `pipeline-execution.json` history consumed by the Overview and future
  pipeline surfaces.
- `backend/Features/Pipeline/RemotePipelineExecutionProjection.cs`: read-time
  bridge for remote cards. It overlays the remote claim and completion facts
  from session/timeline data, the latest Review Plane grade, and canonical
  token-ledger calls onto the normal pipeline catalogue while preserving
  locally recorded integration gates. It never writes
  `pipeline-execution.json` or another lifecycle state.
- `contracts/TaskServer.Contracts/OrchestrationContracts.cs`,
  `task-server/TaskServerOrchestrationStore.cs`, and
  `orchestrator-engine/`: the separated flow boundary. Project flow
  definitions are versioned Task Server data. The API-only Engine executes
  ReviewDecision, Council, PostProcessing, GateDispatch, and CompletionJudge
  stages under bounded per-stage concurrency. A run snapshots the definition
  version and ordered stages at creation, so later definition edits do not
  rewrite in-flight work. The code-owned default definition is version zero;
  the first project override becomes version one. Successful cleanup of a
  canonical Remote Review report creates the decision run transactionally.
- `backend/Features/TestRuns/`: the separate project test-run lifecycle. These
  runs belong to commits rather than cards and expose planned order, scope,
  host, state, result, duration, and derived card attachments through
  `GET /api/projects/{project}/test-runs`. They do not replace per-task pipeline
  step telemetry.
- `backend/Features/Pipeline/RemoteDeliveryIntegration.cs` and
  `backend/Features/Pipeline/MergeIntoDevelopRunner.cs`: the immediate Remote
  admission policy, per-project delivery-order queue, and common
  `post-merge-into-develop` mutation boundary. A settled immutable Result
  Envelope whose Remote Review is `Pass` enters the queue when build/test is
  passed or explicitly not applicable. A frozen plan that requires build/test
  cannot omit that verdict. The report endpoint awaits the result before moving
  Auto Review to Human Review. This is the canonical path for every Remote
  coding project; it is not limited to AGT, a specific executor, or a project
  feature flag. `RemoteExecutionEnabled` controls dispatch only and does not
  change integration admission once a canonical Remote Review report exists.
  A rejected Result Envelope, Review outcome, or build/test verdict records a
  typed `delivery-gate-failed` integration step before the card enters Human
  Review, so the card shows a visible failed integration round rather than
  silent `pending`. The common runner performs serialized merge,
  containment checks, mechanical behind-base recovery, the pre-develop build
  gate, rollback, conflict evidence, and push hand-off. Recovery replays the
  delivery in a disposable detached worktree with `rerere` disabled and proceeds
  only after conflict-free application, exact SHA mapping, and verified cleanup.
  When the configured target resolves to `main` and the repository also has a
  `develop` line, immediate integration never merges the delivery ref into
  `main`. It first requires `main` to be an ancestor of synchronized `develop`,
  merges and gates the delivery on `develop`, runs the release gate on that exact
  develop merge commit, and fast-forwards `main` to the same commit. Existing
  divergence blocks before the delivery merge. A main-only repository retains
  the single-target release path.
  Human acceptance is a quality and sight-review decision only. For a coding
  delivery, `TaskTransitionService` validates that `review-subject.json` still
  names the current Attempt and that Git already projects the delivery as
  `integrated`. It then performs only the lane move. Acceptance never fetches,
  merges, gates, resets the merge row, or enqueues `AcceptedIntegrationQueue`.
  An unintegrated delivery returns the typed `IntegrationFailed` move outcome
  (HTTP 409 for the single-card API) and remains in Human Review with its
  existing gate, conflict, or integration evidence unchanged. Repeated
  acceptance of an integrated delivery is idempotent with respect to Git and
  pipeline history. `operatorOverride: true` is the explicit,
  target-Completed-only exception; no-branch task metadata is exempt without an
  override.
  `DeliveryRefResolver` chooses the immutable result ref first, then an
  attributed commit branch, then `runner/<runner>/<task-key>`, with
  `task/<slug>` only as the legacy local fallback. Remote delivery is fetched
  from origin and fenced to `ResultSha`. Immediately before a real merge or release gate, the
  configured integration ref is fetched again and fast-forwarded; a missing
  local branch is created from origin and a divergent one fails visibly. The
  outcome is recorded so the pending step
  flips to passed / failed / skipped in place. After a successful merge it
  also pushes the integration branch itself to `origin`
  (`post-merge-into-develop-push`, AGT-1999) so integration is never only local:
  the push is offloaded via `IntegrationPushQueue` / `IntegrationPushWorker`
  (`PushIntegrationBranchAsync`, the same "not on the request path" strategy as
  the completed-job workspace push), a transient failure retries with backoff per
  the AGT-1944 environmental-retry taxonomy, and a spent budget records a visible
  `Failed` step flagged `environmental`. Default-on; opt out per project via the
  step's `PipelineSteps` override. The origin push primitive is
  `GitService.PushIntegrationBranchAsync` (non-force; a diverged remote is
  reported, never overwritten). A queued `main` publication in a dual-line
  repository pushes the approved SHA to `develop` first and does not push
  `main` when that prerequisite fails. The intermediate success is not recorded
  as a terminal push step, so restart recovery repeats the ordered pair if the
  process stops between the two pushes.
  An `AlreadyMerged` replay is not gate evidence. When the pre-develop build
  gate applies, recovery requires a durable
  `post-steps/pre-develop-build-gate-N.log` receipt whose expected and tested
  SHA both equal the exact integration tip being released. A missing, partial,
  or SHA-mismatched receipt reruns the gate against that tip. Until the gate
  returns green, the merge step stays pending and no push request is released;
  a red recovery gate records `GateFailed` and leaves the existing branch graph
  unchanged for manual repair. If the gate applies but its runtime component is
  unavailable, recovery also fails closed without releasing a push. This closes
  the BP-02 merge-commit-before-gate crash window without treating Git ancestry
  as a verdict.
- `runner/RemoteReviewWorkspace.cs` and
  `contracts/TaskServer.Contracts/ReviewContracts.cs`: exact-subject remote
  verification. A frozen command is either a deterministic tool command or a
  read-only semantic aspect call. Agent calls use the configured CLI, model,
  and thinking level only when the Review Host advertises matching CLI and
  provider-authentication capabilities. Test commands marked
  `CompareToBaseline` compare their parsed
  failing-test set with the merge-base on the plan's integration ref. The
  parser recognizes .NET, Jest, Karma, Vitest, native Node test, and npm
  lifecycle output, normalizes ANSI-decorated names and volatile Jest file
  durations, and versions its cache entries when parsing semantics change.
  Baseline results are single-flight cached by repository, baseline SHA, and
  command hash. Only failures still new after one subject retry block the
  review.
- `backend/Features/Runner/RemoteReviewPlanBuilder.cs` freezes the effective
  build profile and enabled pipeline aspects into the initial ReviewSubject.
  Auto-discovery reads only Git-tracked entry points, manifests, and
  conventional lockfiles, never working-tree-only folders. A
  `ReviewInfra / PreparationFailed` retry rebuilds the plan from the current
  registered source instead of inheriting the failed preparation layout; other
  infrastructure retries retain the frozen plan.
  `backend/Features/Runner/RemotePipelineReviewEvidenceProjector.cs` projects
  accepted, fenced command evidence back into the ordinary
  `pipeline-execution.json`, aspect Markdown/JSON, file provenance, and
  timeline contracts. The projection does not execute work or decide a lane.
- `backend/Features/Runner/ReviewBaselineBranchPolicy.cs`: which integration
  line the baseline merge-base is taken from. Project truth outranks the card:
  configured project integration branch, then the registered checkout's
  `origin/HEAD`, then the card's recorded `integrationBranch`, then `develop`.
  The card field is only a snapshot from worktree preparation and goes stale -
  AGT-2220 still carried `refs/heads/main` after develop became the working
  branch (30.07.), so every baseline resolved to an ancient merge-base the
  verify commands could not run on. The claim endpoint re-stamps the plan's
  `IntegrationRef` on every hand-out (a frozen plan otherwise replays the stale
  ref through each retry) and rewrites an outdated card field, emitting
  `integration_branch_corrected`.
- `backend/Features/Runner/ReviewInfrastructureRepeatPolicy.cs` and
  `contracts/TaskServer.Contracts/ReviewInfrastructureDiagnosis.cs`: a repeating
  infrastructure cause must name itself. Every runner-side `BaselineUnavailable`
  carries the base commit, integration ref, step, and command line in its reason;
  from the second identical classification in one retry chain the monolith writes
  a `review_infrastructure_repeat_diagnosed` timeline entry with those facts and
  folds them into the escalation reason once the retry budget is spent. Without
  it a drained budget leaves only N identical classifications and no statement of
  what actually failed.
- `runner/ReviewStateStore.cs`, `runner/DurableReviewProcess.cs`, and
  `runner/RemoteReviewExecutor.cs`: durable Remote Review execution. Workspace
  preparation persists the immutable subject and lease/fence before the review
  plan starts. The detached worker atomically records process identity, command
  checkpoints, and terminal evidence. A replacement daemon adopts only a
  positively proven process generation and submits the same attempt through the
  deterministic `review-report:<attempt>:<fence>` key.
- `runner/ReviewWorkspaceRetention.cs`: review workspace retention. The
  executor removes an attempt workspace immediately only after the Task Server
  accepts its terminal report. The daemon also sweeps inactive attempt
  directories older than 72 hours once per hour. Active resource namespaces,
  the reusable `.baseline-cache`, reparse points, and unrelated directories are
  never deletion candidates.
- `AcceptedIntegrationBackstopHostedService` is a compatibility and restart
  recovery path, not an acceptance trigger or the normal Remote integration
  path. It re-drives only remote and local deliveries whose durable Human Review
  `integrating` phase was written by an older backend process before this
  contract took effect. New acceptance requests never create that state. It
  orders recovered deliveries by project
  and original delivery time. The channel is only a latency optimization;
  phase, pending marker, pipeline record, and timeline are the durability
  boundary. Recovery consumes the same
  `TaskIntegrationStatusService` target-branch verdict as the board, so a stale
  Passed step cannot overrule missing Git presence. The backstop finalizes
  Completed only after successful integration and returns decided failures to
  Human Review. Its 15-minute sweep reports `attempted`, `merged`,
  `alreadyMerged`, and `failed` separately; `MergedAfterRebase` contributes to
  the existing `merged` and `integrated` counters. The same sweep evaluates the
  30-minute accepted delivery invariant. The alert evaluates only accepted
  terminal cards in Completed or Archive, never cards still in Human Review,
  whose acceptance has a native integration receipt and no historical
  verification of any supported class. Verified legacy integration, explicit
  no-code delivery records, and reconciled pre-attribution cards never become
  acute alerts. Each alert refresh bypasses the board snapshot cache so an
  externally appended verification converges within one backstop interval.
  Accepted cards without Git-proven integration publish a project-filtered snapshot at
  `GET /api/pipeline/accepted-integration-alert`, render a persistent board
  banner capped at ten task keys with a link to the exact filtered board list,
  and emit a warning event containing the affected task keys.
  Startup first classifies missing historical records in bounded background
  batches, then inventories only `content-on-fence`, `genuinely-missing`, and
  current recorded `Error` or `NoTaskBranch` outcomes. The historical report
  retains counts for all six classes but includes task keys only for its two
  operator-facing classes. Recovery starts only after this bookkeeping pass;
  its records are never merge authority and their cards are never moved by the
  recovery backstop.
- `IntegrationPushBackstopHostedService` reconstructs lost
  `IntegrationPushQueue` work from durable passed-merge and pending-push
  pipeline facts. The channel is a latency optimization, not the durability
  boundary.
- `backend/Features/Pipeline/TaskSpawnerPostStepRunner.cs` (+ `TaskSpawnerDecision.cs`,
  `TaskSpawnerModelSelector.cs`, `SpawnedTaskLedger.cs`): the opt-in
  `post-task-spawner` step (AGT-2028). After a task settles it asks the best
  available model whether the change set is relevant to a configured target
  project and, on a conservative yes, creates a follow-up card there via
  `TaskMutationService.CreateJob` with a `relatedTo` back-reference. Generic (not
  website-hardwired): target project, relevance question, and spawn lane come from
  `ProjectSettings.TaskSpawner`. Driven from `ReviewDecisionOrchestrator`
  (`RunTaskSpawnerPostStepAsync`); template `prompts/runtime/task-spawner-relevance.md`.
- `backend/Features/Pipeline/AgentsWikiSyncPostStepRunner.cs`: the opt-in
  `post-agents-wiki-sync` step (AGT-1782). Deterministic (no LLM): it keeps the
  AGENTS.md -> wiki pointers for a set of designated topics consistent (no dead /
  missing link) and maintains a machine-owned "Current State / Progress" page per
  designated topic under `docs/concepts/designated-topics/`, so agents read
  the current state of a topic instead of re-discovering it ("gegen im Kreis
  drehen"). The operator-owned topic list is `designated-topics/registry.json`
  (self-provisioned as an empty template on first run); a task is matched to a
  topic by a shared tag or a changed-file path prefix, and the per-topic
  current-state line is derived from the task's title / newest commit / typed
  outcome. Driven from `ReviewDecisionOrchestrator` (`RunAgentsWikiSyncPostStep`),
  next to the wiki-maintenance / wiki-learnings producers.
- `backend/Services/Pipeline/PipelineStepConfigResolver.cs`: effective model and
  step config resolution.
- `backend/Shared/Models/PipelineTypes.cs` and
  `backend/Features/Pipeline/PipelineTypeSettings.cs`: resolve each card to the
  extensible `task`, `bug`, `feature`, or `planning` settings dimension and
  project that type's step overrides and order before runtime resolution.
- `backend/Features/Projects/ProjectSettingsService.cs`: persists typed
  pipeline overrides and migrates legacy flat settings into all three coding
  types. Planning deliberately starts from its lightweight defaults.
- `backend/Features/Pipeline/TestSelectionPlanner.cs`: staged test planning from
  the lane policy, changed files, project/component ownership, explicit impact
  rules, and Test Hub history. It produces the immutable selection audit used
  by the gate log.
- `backend/Features/Pipeline/LlmTestSelectionAdvisor.cs`: optional constrained
  adviser. It can add only stable candidate ids from the deterministic safe
  inventory and cannot emit an executable command.
- `backend/Features/Pipeline/PreMainTestGate.cs`: fail-closed release boundary
  that forces the full test level before a configured merge can advance
  `main`, irrespective of lane settings, diff input, history, or adviser
  output.
- `backend/Features/Pipeline/PreDevelopBuildGate.cs` and
  `FrontendWorkPackagePlanner.cs`: exact-merge develop boundary. Non-frontend
  deliveries retain the build-only level. A merge result that touches
  `frontend/` runs a blocking Angular work package made from every spec in each
  touched source folder plus the fixed app, studio-shell, and task-detail
  barrel collision probes. Broad frontend suite commands stay outside the
  candidate inventory and configured continuous set, so impact rules, history,
  or the optional adviser cannot silently turn this boundary into the promotion
  full suite.
- `backend/Services/Pipeline/PipelineStepConditionEvaluator.cs`: per-step
  condition evaluation.
- `backend/Services/Pipeline/ProjectPipelineOrder.cs`: project-level step order
  handling.
- `backend/Features/Pipeline/ProjectStackDetector.cs`: bounded convention-based
  Angular, .NET, and Node detection from repository markers. Pipeline catalogue
  applicability never reads the configured build-profile stack label.
- `backend/Features/Pipeline/PipelineStepExecutionResolver.cs` and
  `PipelineStepProbeService.cs`: effective shell-command projection and isolated
  per-step probes. Probes do not create or move tasks and execute through the
  build/test gate runner so they share its machine lock.
- `backend/Services/Pipeline/ProjectPipelineCostService.cs` and
  `PipelineCostCalculator.cs`: cost summary projection.
- `backend/Services/Runner/PostAbortReviewStepService.cs` and
  `backend/Services/Runner/PostAbortReview.cs`: abort-review contract and
  deterministic decider.
- `backend/Services/Runner/ReviewDecisionOrchestrator.cs`: post-core review and
  final orchestrator decision recording. `RunCodeReviewGradePostStepAsync` wires
  the automatic quality-grade step (see below) after the aspect fan-out.
- `backend/Features/Runner/ReissuePromptExperiment.cs` and
  `scripts/reissue-prompt-experiment-analysis.mjs`: versioned, reproducible
  task-level control/treatment assignment for eligible finding-bearing
  reissues, hard assignment telemetry, and the right-censor-aware experimental
  report. The treatment changes prompt organization only and never selects a
  coding model, reviewer, rubric, pipeline, or gate. The predeclared contract
  and promotion threshold live in
  [Finding-first reissue prompt experiment](../../quality/pipeline-time-economy/reissue-prompt-experiment.md).
- `backend/Services/Review/CodeReviewStepService.cs`: the shared code-review
  engine. `CodeReviewMode.Verdict` is the legacy user-triggered pass/concerns/block
  review; `CodeReviewMode.Grade` is the automatic pipeline pass that assigns an
  A/B/C/D quality grade and writes a rendered `code-review-grade-<ts>.md`.
- `backend/Services/Review/CodeReviewGrade.cs`: grade enum, the
  `[[CODE_REVIEW_GRADE: grade=<A|B|C|D>; summary=<short>]]` sentinel parser, the
  `code-review:grade-{a..d}` tag mapping, and the grade->pass/concerns/block
  severity mapping.
- `backend/Features/Review/CouncilReviewReaction.cs`: structured review-finding
  parsing, the bounded per-finding council policy, targeted follow-up rendering,
  and the reaction sidecar stored beside each automatic grade artifact.
- `backend/Features/Review/CodeReviewGradeModelSelector.cs`: resolves the grade
  model/CLI from `CodeReviewStep:DefaultModel` / `CodeReviewStep:DefaultCli`,
  defaulting to Codex's live-discovered flagship (gpt-5.5 fallback) at its top
  advertised reasoning level.
- `backend/Features/Cli/Routing/OneShot/PromptLoggingCliOneShot.cs`: the
  central-dispatch decorator over `ICliOneShot.RunAsync` that captures the raw
  final prompt of every one-shot step-call. `backend/Host/Program.cs` registers
  `ICliOneShot` as this decorator wrapping `ClaudeOneShot`, so wrapping the
  single seam captures every step that opts in by setting `JobFolderPath` +
  `StepId` on its `CliOneShotRequest` (today: the review aspects via
  `AspectRunnerService` and the code-review-grade / verdict passes via
  `CodeReviewStepService`).
- `backend/Features/Cli/Routing/OneShot/CodexOneShot.cs`: read-only Codex JSONL
  adapter for model-backed pipeline steps. A project can opt an aspect or the
  abort-review step into Codex through its existing `PipelineSteps` CLI/model
  override, including a live-discovered Spark model, without changing the core
  coding run. Model-backed review/pipeline defaults now use Codex/OpenAI routes.
- `backend/Features/Cli/Routing/OneShot/StepPromptLog.cs`: the per-job
  append/read writer for `.metadata/prompts.jsonl` (see filesystem-contract).
  Writes through the shared `IJsonlAppender` (concurrent aspect fan-out cannot
  interleave bytes); reads parse the file back into the step-prompt read-model,
  skipping blank / unparseable lines.
- `backend/Features/Tasks/TaskPipelineEndpoints.cs`: API surface for task
  pipeline data, including `GET /{jobId}/step-prompts`, the read-model the
  Overview "Prompt" affordance parses from `.metadata/prompts.jsonl`.
- `backend/Features/Tasks/TaskLiveStatusProjection.cs`: board and detail
  read-model for the current pipeline step, recorded CLI/model provenance,
  enabled upcoming steps, current runner/review queue position, and latest
  activity time. It reads the current execution root only.
- `frontend/src/app/features/task-pipeline/` and the task-detail Overview:
  pipeline presentation.

## Invariants

- Pipeline settings are resolved from the card before enablement, ordering,
  model, prompt, condition, gate, deferred merge, or push decisions. Generic
  coding work, bugs, and features have independent override maps even though
  they currently share the standard catalogue defaults. Planning and research
  use the lightweight planning chain and never inherit migrated coding
  overrides. Concept retains its dedicated document-first catalogue.
- The task pipeline endpoint projects local and remote lifecycle facts at read
  time. A remote claim/completion becomes CORE work. Accepted Review Plane
  command evidence becomes the corresponding TOOL or ASPECT execution, and its
  grade becomes the DECISION verdict. Recorded integration gates remain TOOL
  steps. PRE, DRIFT, task-store mutation, and Studio-seat probes that the remote
  route structurally omits are `Skipped` with an explicit
  remote/not-applicable reason and are projected as `Not applicable` in the
  Overview. `Not run` is reserved for a step the current attempt genuinely
  never reached. Remote token totals, historical list-price estimates, and call
  counts come from the same token ledger as the Task tab.
- Test execution has three stable levels: `continuous` runs the configured
  fixed baseline, `work-package` adds tests selected from the current diff and
  Test Hub history, and `full` runs every declared test command. Project
  settings map task lanes to levels. Auto Review defaults to `work-package`
  when no mapping exists; an unavailable diff falls back to `full`. A configured
  continuous baseline also runs for documentation-only diffs, and an explicitly
  required `full` level can never be bypassed by the no-code-diff optimization.
- The pre-develop gate derives changed files from the exact merge commit and
  its first parent. A missing diff fails closed and rolls back a merge created
  by that attempt. Frontend paths force a blocking `work-package` level even
  when no project build profile exists; non-frontend paths keep `build-only`.
  Focused Angular includes cover touched source folders and the fixed collision
  set (`app.spec.ts`, `studio-shell.component.spec.ts`, and
  `task-detail.spec.ts`). Generated .NET work-package commands preserve an
  explicit test filter or default to `Category!=MachineBound`, keeping
  machine- and Windows-bound process/timing families out of develop admission.
  Only the pre-main promotion boundary may force `full`.
- The build/test step reason always states the effective level, selected count,
  whether the full suite ran, and how many full-suite commands were omitted.
  The task Overview exposes that reason from the passed status icon as well, so
  a green work-package subset cannot be mistaken for a full-suite pass.
  Its `post-steps/build-test-gate-*.log` contains the exact diff input, history
  rows, candidate inventory, chosen ids/commands, selector/model, and reasons.
  `FullSuiteRan` is execution evidence, not a planning claim: it becomes true
  only after every selected full-suite test command was attempted.
- Disposable local and remote gate workspaces prepare dependencies before any
  derived verification command runs. An explicit build profile `installCmd` is
  authoritative. Otherwise a selected root .NET entry point gets `dotnet
  restore`, and every selected Node package directory gets `npm ci`. Local npm
  commands set `NPM_CONFIG_CACHE` to the machine's shared Agent Studio npm cache;
  remote npm commands use the executor user's equivalent cache. Exact-subject
  local gates also keep one dependency cache per source repository under
  `agentstudio-review-gates/.dependency-cache`: `node_modules`, `.angular`, and
  `.nm-state` move into the new disposable worktree before verification and move
  back before Git cleanup. The shared `DependencyPreparationState` compares the
  build profile lockfiles, or conventionally discovered npm lockfile, with
  `.nm-state`; an unchanged hash skips the install, while a missing or changed
  dependency state runs the install and stamps the new hash. Explicit profile
  commands, including `installCmd`, use the same `bash -lc` contract as
  build-profile validation on every host. Convention-derived commands retain
  the host shell.
- Immutable Remote Review plans carry that same preparation command, lockfile
  scopes, and preserve globs to the Review Executor. Preparation runs before
  verification in both the candidate and any materialized baseline workspace.
  Candidate and baseline caches use distinct role namespaces while sharing the
  same transfer and lock-digest protocol as local exact-subject gates. A failed
  preparation, including a missing toolchain, is `ReviewInfra / PreparationFailed`;
  it is never a product verdict. Its exact command, exit code, named budget, and
  complete stdout and stderr artifacts are retained in the Remote Review grade.
  A missing preparation directory is also copied into the grade Detail line so
  the path is visible without opening stderr.
- Dependency preparation and verification consume one shared `gate-run` budget.
  Machine-gate queue wait, exact workspace materialization, and exact workspace
  cleanup have separate named budgets. Local Git operations receive the remaining
  budget from the owning workspace phase and never fall back to the Git helper's
  per-process default. A timeout reason, structured completion event, and durable
  gate receipt identify the violated budget with its limit, consumption, and
  phase. The receipt also includes dependency-cache hit/miss evidence.
- A failed preparation or verification command stores a bounded, single-line
  stderr/stdout excerpt in the gate reason that flows into the durable pipeline
  step record. Full streams remain in per-process evidence and the gate log, so
  an exit code is never the only recorded diagnostic.
- An empty verify plan is a separate terminal class: the runner records
  `BuildTestGateVerdict.NotApplicable`, the pipeline stores
  `PipelineStepStatus.NotApplicable`, and every operator surface renders the
  neutral `No build/test defined` meaning. `Skipped` remains reserved for a
  configured or interrupted gate that did not run and therefore stays an
  attention state. The pre-develop BP-02 backstop accepts only `Ok` or
  `NotApplicable`; it never treats `Skipped` as green.
- A failing continuous-baseline command during a work-package run creates a
  separate `post-steps/test-findings-*.json` record and a `warn` gate verdict.
  It does not block the card. Selected work-package tests still block. No
  failure is non-blocking at the pre-main full-suite boundary. If one physical
  command belongs to both the baseline and the diff-selected set, the stricter
  work-package classification wins.
- A remote ReviewAttempt does not require an historically red integration
  branch to become absolutely green. For each baseline-compared test command,
  its verdict is based on `subject failures - merge-base failures`.
  Intersecting failures remain visible as pre-existing, while the aspect summary
  names every new failure. The Review Executor reads xUnit
  `Category=ReviewFlaky` traits from the exact subject's built test assemblies.
  A newly failing marked test is retried once; if it does not reproduce, the
  report retains its identity as `FlakyQuarantine` and does not classify the
  card as `ProductFailure`. A reproduced marked failure remains a blocking new
  failure. A command with unparseable failing-test output stays fail-closed as a
  new failure. This comparison does not weaken the absolute full-suite boundary
  before advancing `main`.
- Remote Review command execution survives a planned Review daemon restart.
  Recovered attempts retain their original fence and containment namespace and
  resume before load-aware admission evaluates any fresh slot. Completed
  commands are not relaunched. If process adoption cannot be proven, the
  attempt ends visibly as `ReviewInfra / ExecutorRestarted` with the failed
  proof, completed-command count and duration, and retry reason. Replaying the
  fixed report key with another terminal payload is rejected.
- Model advice is additive and allowlisted. Deterministic diff/history choices
  cannot be removed, unknown candidate ids are ignored, and raw model output is
  never interpreted as a shell command.
- Any operation that can advance `main` must call `PreMainTestGate` first and
  proceed only on an `Ok` result with `FullSuiteRequired` and `FullSuiteRan` set.
  `PreMainTestGate` converts a nominally green runner result without that
  evidence into a failure, so callers cannot accidentally accept an incomplete
  release check. It also forces exact-subject execution even if the caller
  supplied a weaker request. The existing deferred integration merge is an
  enforced caller when its configured target resolves to `main`: in a dual-line
  repository its source is exclusively the exact `develop` merge commit, never
  a raw delivery fence. It verifies that the candidate descends from `main`, runs
  the full suite once on that exact source SHA, records
  `post-steps/pre-main-test-gate-*.log`, rechecks both branch tips after the
  suite, and only then fast-forwards `main`. A red or incomplete result leaves
  `main` unchanged. The future manifest-based release workflow must use the
  same boundary.
- Framework-specific catalogue steps declare `appliesTo`; `any` remains the
  default. The project catalogue response includes derived `detectedStacks`, an
  `applicable` flag, and the effective resolved command list for every step.
  Inapplicable steps remain visible in Project Hub -> Pipeline.
- A project-level step probe is diagnostic only. It may run the step's resolved
  shell command against the repository, but it never creates a task or changes a
  lane. Every shell probe is serialized by the build/test machine lock.

- `pre-model-qualification` runs before CORE and never performs quota fallback
  routing. It recommends from the live CLI catalogue without hardcoded model
  ids. Explicit card model/reasoning pins always win; legacy cards without
  provenance are treated as pinned. The selected/recommended pair remains
  visible on the step record.
- `pre-prompt-enrichment` runs after qualification and before CORE spawn. The
  original task block remains byte-for-byte readable and the labelled
  enrichment is additive inside the existing mode-framing seam. A worktree
  containment notice may still precede the task block. Its step token buckets
  describe selector work only, which is zero in the deterministic
  implementation. Appended prompt tokens are attributed in
  `enrichment-report.json` and remain part of CORE input, so pipeline cost
  totals do not count them twice.
- Cheap-model routing is explicit and reversible. `PipelineStepSetting` owns the
  `(cliType, model, thinkingLevel)` override per project and step; absent fields
  preserve the current runtime default. Aspect reviews and abort review honor
  all three fields. Spark model ids are selected from the live Codex catalogue,
  not pinned in the static registry, because the entitlement model can change.
  Setting `economyModel: true` on an aspect activates the automated
  `TokenEconomy.SuggestModel` path against the live Spark subset. The
  `pre-model-qualification` step (AGT-2146) remains the evidence-producing guard,
  explicit step pins continue to win, and a missing Spark candidate preserves
  the current runtime default. Coding CORE runs are not routed to Spark by this
  feature.
  Aspect output validation is unchanged and deterministic across models: valid
  sentinels map to the three aspect statuses, while a malformed Spark reply maps
  to `Concerns` plus `review:unparseable` through the existing parser path.
- Eligible mapped reissues participate in `finding-first-v1` at the task level.
  The stable hash assignment keeps all attempts for one task in the same arm.
  Both versioned arms receive the identical open-finding payload and preserve
  scope and terminal-sentinel guardrails. Assignment and attempt events are hard
  telemetry; Grade A and orchestrator acceptance remain model-judged evidence;
  arm effects are experimental comparisons. Production-default promotion is
  forbidden until the predeclared benefit and deterministic-gate safeguards
  pass.
- Board cards and task detail share one live-status projection. The active step
  comes from the newest root `PipelineExecutionRecord`; `PreviousAttempts` is
  never eligible for a current-work or inactivity signal. CLI/model labels come
  from the recorded step or matching `StepPromptLog` entry, host identity comes
  from the existing execution-location projection, and queue positions come
  from the runner and post-processing queues that already schedule the work.
  The projection is read-only and introduces no telemetry or persisted task
  state. A Ready task treats an existing completed root as the previous attempt
  and previews the fresh enabled chain. Without an active step or queue
  position, active-lane cards report the newest recorded activity time and
  explicitly flag ten minutes of silence as a possible hang.

### Execution placement matrix

The Remote-Ready direction is that execution follows the immutable subject to
an eligible Agent Host. It does not grant that host Task Server write authority.
This is the current application of the
[Remote-Ready line](../../concepts/review-pipeline-health/decision-history.md)
and the exact-subject, per-step placement contract in
[Runner provenance](../../concepts/completion-review-and-remote-runner-stability.html#provenance).

| Step class | Current placement for a canonical Remote task | Remote-capable? | Boundary and reason |
|---|---|---|---|
| Deterministic repository tools, including restore, build, test, lint, and subject inspection | Agent Host, in the ReviewAttempt task worktree at the exact Result-SHA | Yes, default for the frozen Remote Review plan | They need the repository, declared toolchain, and host caches, not Studio state. Admission uses the existing Review Executor capacity, capability, lease, fence, and resource namespace. Missing CLI or toolchain is infrastructure failure, never a local fallback or product verdict. |
| Semantic aspects (`aspect-*`) | Agent Host, in the same exact-subject ReviewAttempt workspace | Yes, default when the aspect is enabled | The plan freezes prompt, CLI, model, and thinking level before claim. The Host must advertise matching CLI and provider-authentication capabilities. Execution is read-only and clean-context; the accepted report produces the same `aspect-*.md`, `aspect-*.json`, file provenance, status, tokens, and verdict as local execution. |
| Decision assistance | Evidence generation runs on the Agent Host; orchestration synthesis and the binding decision remain in the control plane | Split | Repository-reading evidence belongs with the exact subject and is reported through the fenced ReviewAttempt. Combining all evidence, applying retry budgets, requesting reissue/escalation, and changing a lane require the canonical task history and Task Server authority. These operations must not run in a disposable repository worker. This is a control-plane boundary, not a requirement that the control plane remain on the Studio workstation. |
| Review build/test gate | Agent Host through a claimed and fenced ReviewAttempt | Yes, canonical current path | The immutable plan carries deterministic build and test commands to the exact-SHA workspace. Evidence is attributed per step under the ReviewAttempt lease and fence. There is no SSH transport or local fallback for canonical Remote review. |
| Integration and release gates (`pre-develop`, `pre-main`) | Integration owner through the in-process exact-subject gate | Not remotely claimable yet | The tested subject is an integration commit created inside the serialized merge/rollback boundary, not the original task Result-SHA. The integration owner retains transaction ordering and rollback authority. The approved [W18 Remote Gate target architecture](../../operations/remote-gate-zielbild/index.html) remains a dedicated `GateSubject`/`GateAttempt` claim, with no SSH transport or silent remote-to-local fallback. |
| Task/project mutation tools, including managed docs/wiki writes, task spawning, commits, integration, push, and lane mutation | Task Server or platform-owned integration boundary | No as an ordinary repository step | They need canonical task/project paths, append-only ledgers, global serialization, API authorization, or platform-owned Git durability. An Agent Host may return evidence or a recommendation, but it cannot write the task namespace or perform the side effect from its disposable review worktree. |
| PRE qualification/enrichment and cross-project DRIFT checks | Control plane | Not in the Remote Review worktree | PRE freezes policy and task context before dispatch. DRIFT reads designated-topic and cross-project state and may lead to centrally deduplicated follow-up work. Neither is an exact-subject repository review command. |
| Studio-owned UI probes and operator-desktop evidence | Studio seat | No | They depend on the locally supervised stable browser, loopback Studio origin, Windows-native dev seat, or operator-visible desktop session. Headless browser tests that can declare an immutable repository subject, browser/toolchain capability, and self-contained artifacts are ordinary remote tools; a probe of the Studio seat itself is not. |

ReviewAttempt is the canonical current path for deterministic Remote review
tools, build/test commands, and semantic aspects. W18 may later provide a
first-class claimable GateAttempt for integration and release gates so their
queueing, retries, cleanup, and terminal infrastructure state are independently
visible without consuming review identity.

Every remotely executed command records `executionLocation=remote`, Host,
Executor, ReviewAttempt, lease/fence-derived authority, start/finish time, exact
SHA/tree, and, for model calls, model, thinking level, and token usage. The
pipeline row stores the same placement fields. Timeline start and finish events
repeat the actual location and identity for operator inspection. Legacy rows
without placement remain unknown; the UI must never infer `local` merely from a
missing active lease.

### Post-step lifecycle and ownership

The separated control-plane path preserves the same ownership rule:
definitions live in the Task Server, while execution lives in
`orchestrator-engine`. The Engine receives the task payload and prior stage
results only through the public API. Its `engine.env` contains bootstrap
connectivity, identity/credential, lease timing, and concurrency caps, never
project flow definitions, model routing, or gate policy. HTTPS remains required
off-loopback. The source Compose stack may set `ALLOW_INSECURE_HTTP=1` only for
the authenticated, contained Docker network between Engine and Task Server;
the option does not belong in a remote-host `engine.env`.

A valid Remote Review report is evidence, not a lane decision. Infrastructure
outcomes stay in Auto Review and retry the same immutable subject. A valid
product report also stays there until cleanup proves the disposable review
workspace is gone. That cleanup atomically appends one idempotent orchestration
run whose payload binds the coding RunAttempt, ReviewSubject, ReviewAttempt,
Result-SHA, policy hash, report hash, verdicts, and gate facts. Only a fenced
Engine settlement can request reissue, escalation, or Human Review handoff, and
only the Task Server can apply the version-fenced lane mutation plus lifecycle
evidence. Studio and its BFF are read and command surfaces, not loop owners.
Requirement-fit receives the card's normalized `acceptanceScope` as a separate,
authoritative prompt section. For `bounded-slice`, it judges only the named
slice and criteria; a linked Dossier, parent objective, or open recommendation
list is context. Cards without a scope retain full-task review, with a narrow
legacy inference only for explicit one-slice or partial-success declarations.

The lane decision also has a semantic convergence bound. Each blocking aspect,
classification, and summary set is normalized and fingerprinted. The second
consecutive identical Remote Review block escalates the card with the exact
finding instead of reissuing it. Local review journals apply the same rule
within the operator-owned attempt epoch. Changing findings may continue only
within the existing task-wide reissue budget.

Integration is a pipeline-owned delivery decision. The canonical Remote order
is **delivery -> settled Review/build gate -> integration -> Human Review ->
acceptance**. No automatic Post Processing verdict may mark a task Completed;
Human Review remains the quality decision after the code is already integrated.

The named deviations are narrow. Local worktree coding integrates during local
finalization before its Auto Review gate, but still before Human Review.
Planning, research, concept, Epic, and other no-code/no-branch deliveries do not
integrate. Remote Review infrastructure failures remain in Auto Review while
their retry budget is available. A failed product/build gate, merge conflict,
lineage failure, push failure, or configured `pull-request` integration strategy
may enter or remain in Human Review with a visible failed/non-integrated verdict;
acceptance cannot repair it. All currently configured direct-merge Remote coding
projects use the canonical order without a project-name exception.

Result finalization is a distinct awaited post-core gate on both execution
paths. The local application retries only `SummaryGenerationService`; the V1
Task Server exposes `post-result-finalization`, generates the application-owned
`status.md` artifact from durable run events and deliverables, and persists its
typed state in task history. `Retryable` consumes the bounded summary budget
without reopening CORE. `Ready` carries the generated artifact hash.
`Degraded` is terminal for this post-step, keeps the completed run reviewable,
and never presents the transition scaffold as a normal generated Result.

A post-step has four distinct lifecycle states. **Defined** means the code-owned
catalogue knows its id, capabilities, dependencies, and default. **Enabled**
means a project override (or the catalogue default) includes it in future task
pipelines. **Run** means one task has an immutable execution attempt with its
own start, finish, outcome, and artefact reference. **Re-run** appends another
attempt for that same task and step; it does not restart CORE, replace an older
artefact, or rewrite the earlier attempt. The task Overview is the execution
surface, while Project Hub -> Pipeline is the durable activation surface.

Ownership is deliberately layered. The global catalogue owns what a step is
and whether it is available. A project owns the effective default configuration
(enabled, agent, prompt binding, condition, and order). A card owns only its
execution plan and attempt history: an operator may add a catalogue step to an
existing card after creation and run it immediately, without changing the
project default. The Overview must show the effective activation source as
`global`, `project`, or `condition`, with the backend supplying the exact reason
so the UI never re-derives precedence. Its settings link lands on Project Hub ->
Pipeline, the control that can persist a project override; a global default is
code-owned, so that same destination is where an operator overrides it. A
card-level addition is a separate execution-plan fact, not an activation source
or a new arbitrary executable definition; it can only reference a known
catalogue step.

On-demand execution is bounded to post-steps that declare themselves
idempotent and have an implemented runner. It is allowed after the main run and
after the card has reached a terminal lane. Each invocation appends a
timestamped result artefact or an append-only execution entry and records the
CLI-task substrate visibility used by normal pipeline steps. Quality grading is
the first LLM-backed retro use case: it resolves the task-owned branch/commit
range, writes a new `code-review-grade-<timestamp>.md`, updates the current
grade tag, and retains every older grade report. Reporting-only re-runs never
move the card or revise the historical orchestrator verdict.

Deterministic on-demand tools write one immutable task result at
`results/post-steps/<step-id>-attempt-<NNN>.md` and append the matching substrate
row to `logs/step-runs.jsonl`. The result links the project artefact a tool
created or refreshed. This separates the task's audit history from the tool's
idempotent project output, which may legitimately converge on one wiki page.
Attempt numbers are reserved with create-new filesystem markers before a run,
and result files are also create-new, so concurrent requests and process
restarts can leave gaps but can never reuse an attempt or overwrite evidence.
Rows carry the registry-backed `PROJ-NNN` identity, a canonical
`PROJ-NNN::jobId` key, and the schema-defined hash id; mutable display names and
watch paths are not persisted as identity. A project artefact write runs only
against a clean managed checkout, is committed by the platform as one bounded
commit, stamped onto the task, and handed to the completed-push queue. Commit
failure restores only the paths produced inside that boundary; pre-existing
operator changes cause the step to fail before its writer runs.

- Aspect and code-review prompts carry a complete evidence set (AGT-2022): the
  run-window diff summary is appended with the task-branch-vs-base commit range
  (`base..task/<id>` via `GitService.GetCommitsInRangeAtRoot`) so a squash/merge
  or steer follow-up with an empty working diff still shows the real change set;
  the job's `results/` folder inventory (`ResultsInventory.Render`, file list +
  short excerpts); and a one-line card-mode framing (`ReviewCardMode.Describe`)
  so a report-only planning/research card or docs-only concept card is not read
  as missing work. The
  "deliverables missing" verdict is legitimate ONLY when the branch diff is empty
  AND `results/` has no artefacts AND no external deliverable (e.g. a `docs/`
  commit) is documented. `AspectRunInputs` / `CodeReviewStepRequest` carry the
  `ResultsInventory` + `CardMode` fields; the `{{results_inventory}}` and
  `{{card_mode}}` slots render them in every aspect + code-review template.
- A fenced remote completion persists `review-subject.json` with its exact
  `RunAttemptId`, `ResultSha`, delivery ref, and actual integration branch ref.
  A reissue or transition into a new local or remote run invalidates the
  canonical sidecar. Before any already-integrated shortcut, acceptance trusts
  it only when its task key, attempt, and result SHA match the authority store's
  current settled RunAttempt. Both
  `post-build-test-gate` and `post-code-review-grade` use that SHA as their
  authoritative subject. The build gate's selected subject is carried through
  the later aspect and grade steps. The grade reviews the full
  merge-base-to-`ResultSha` task range, not only the result commit, and must not
  fall back to the canonical task-branch HEAD when the runner delivered a
  different commit. Merge and integration projections use the recorded branch
  ref, not the current project default. Otherwise the pipeline could test one
  revision, review another, omit earlier commits from a multi-commit delivery,
  or merge the reviewed result into the wrong line.
- Review-subject task identity follows the flat-storage authority. For a task
  under `tasks/<bucket>/<KEY>`, the validated key is the folder name when its
  numeric bucket matches; this keeps repository-embedded `.orchestrator/jobs`
  storage valid even if a concurrent `task.json` metadata rewrite prevents a
  direct read. Legacy lane storage still reads `key`, `taskKey`, or `id` from
  `task.json` case-insensitively.
- Failed accepted-integration steps persist a machine-readable `failureCode`.
  The board projects that code with concise operator copy and recovery
  eligibility. `merge-conflict` and `source-needs-rebase` offer focused rebase
  recovery for legacy or explicitly operator-owned cases. A current immediate
  integration that cannot retain a one-to-one delivery commit mapping records
  `delivery-attribution-ambiguous` and starts one bounded automatic steer round
  before Human Review. Task-key or review-subject validation failures stay
  visible but do not offer an unrelated rebase action. The raw pipeline reason
  and timeline event remain the detailed evidence.
- `post-orchestrator-review` is an early completeness gate. It must never render
  as a final verdict.
- `post-orchestrator-decision` is the single final orchestrator verdict.
- Automatic quality-grade reviews follow the council contract. Every grade
  artifact receives an explicit orchestrator reaction. Grade A with no named
  deficiencies records `Accept, nothing open.` A review that names concrete
  deficiencies records one `FixNextRound`, `Accept`, or `Escalate` assessment
  per finding. `FixNextRound` reissues the same card within the shared loop
  budget and writes only the selected finding sentences to
  `orchestrator-follow-up.md`; exhausted budget escalates every remaining
  finding. A B/C/D response without the required concrete finding sentences is
  never treated as clean: it escalates the missing handoff because no safe,
  targeted round can be formed. When a deterministic build/test failure already
  reopens the same attempt, that follow-up includes both the build output and
  the selected council findings. The sibling `*.council-reaction.json` and the
  action decision journal entry are the read-side chain for review -> reaction
  -> target task/run. Task-detail renders this reaction on the review row. A
  legacy or manually triggered review without a sidecar shows an explicit
  `No orchestrator reaction recorded` audit state instead of silently omitting
  the reaction.

  This is a load-bearing review-orchestration contract, not optional reporting.
  The terminal routing is fixed:

  | Review outcome | Orchestrator reaction | Lane effect | Required durable evidence |
  |---|---|---|---|
  | Grade A, no findings | `Accept, nothing open.` | Continue through the remaining gates | Reaction sidecar on the grade artifact |
  | Named findings, loop budget available | One `FixNextRound` assessment per finding | Reissue the same task to `2-ready` | Sidecar, decision-journal record, targeted `orchestrator-follow-up.md`, and target task/run |
  | Named findings, loop budget exhausted | One `Escalate` assessment per finding | Move to `5e-escalated` | Sidecar and decision-journal record |
  | Grade B/C/D without concrete finding sentences | Escalate the missing handoff | Move to `5e-escalated` | Sidecar explaining why no safe targeted round can start |

  A task is not accepted merely because the letter grade is passing. Named
  findings take precedence over the grade letter. Completion, build/test,
  evidence, solution-quality, and council decisions share the same bounded
  reissue budget; the council reaction runs before generic evidence routing so
  its concrete finding sentences remain the next-round assignment.
- `post-code-review-grade` is the automatic quality-grade step (ASS-1657). It is
  `DefaultEnabled`, runs after the four aspect reviews and before
  `post-orchestrator-decision`, and assigns every pipelined task an A/B/C/D grade
  with the rubric: A solves the goal completely with tests/evidence, B is solid
  with small gaps, C has concerns (half-done/unclear), D misses the goal or
  redundantly redoes existing code. The grade token is reporting-only: it
  surfaces as a `code-review:grade-{a..d}` card tag plus a rendered detail file.
  A D records a `Failed` step row so it stands out in the Overview, and A-C
  record `Passed`. Named findings from that review are inputs to the separate
  council decision above and can therefore start a bounded round. The grade
  model is quality-first: it defaults to the
  live-discovered Codex flagship with the top supported reasoning level
  (`CodeReviewStep:DefaultModel`, CLI `CodeReviewStep:DefaultCli`), while the four
  bounded aspect reviews use Codex `gpt-5.4-mini` at `high`. Opt out per deployment
  with `CodeReviewStep:AutoGrade=false`. An
  unparseable reply degrades to grade C, never silently A.
- The grade is reporting evidence, not a success gate. It therefore runs before
  the red build/test-gate reissue branch and before the aspect-infrastructure
  escalation branch. A grade transport/runtime failure records `Failed` with its
  error and clears stale `code-review:grade-*` tags, but never changes the lane
  decision.
- Completing a pipeline terminalizes every known, non-deferred, non-stub row:
  an unreached `Pending` row becomes `Skipped` with the branch's causal reason,
  while an interrupted `Running` row becomes `Failed`. Deferred merge/push rows
  remain `Pending`, catalogue stubs remain `Planned`, and unknown extension rows
  are preserved. `PipelineExecutionLog.Read` applies the same projection purely
  to legacy current and previous attempts without rewriting their JSON files.
- Task detail renders pending, non-deferred rows as `Not run` when a settled
  attempt used a lightweight path or escalated before the full pipeline ran.
  Deferred rows remain `Pending`. The Result view has one verdict badge, and
  every human-review escalation writes a minimal Result scaffold before moving
  the task so preparation failures retain durable evidence.
- A missing / unparseable aspect verdict caused by the reviewing CLI dying (the
  backend cut that killed the aspect runner mid-run) is an ENVIRONMENTAL infra
  fault, never the card's unfinished work (AGT-2021, belege AGT-1996). The aspect
  runner reruns that step exactly once with the AGT-1944 environmental backoff
  (`PostProcessingOutcomeTaxonomy.DecidePostStepVerdictRetry`,
  `MaxPostStepVerdictRetries` = 1); only when the retry again yields no output is
  the verdict flagged `AspectVerdict.IsInfraFailure`. The orchestrator then
  short-circuits before the accept / reissue routing and escalates the card
  flagged `environmental` + `InfraCrash` as a chain-ending `Escalate` decision, so
  the reissue budget is NOT charged (`ReviewDecisionOrchestrator.HandleAspectInfraCrashAsync`).
  A CLI that DID reply (even garbage) is not infra: it keeps the existing
  `review:unparseable` concern. The other post-steps
  (`post-code-review-grade`, wiki-maintenance / wiki-learnings, regression-radar)
  are reporting-only and already swallow a crash into a Skipped/Failed step row,
  so a post-step crash there never gates the lane or counts as a work deficit.
- `post-build-test-gate` verifies a coding task in its registered
  `task/<id>` worktree when that worktree is live. It must not build in the
  shared project checkout for a worktree run: a dev backend can legitimately
  hold that checkout's build output open, and the shared checkout can contain
  different source. Sequential and legacy runs with no registered worktree
  retain the shared-checkout fallback. Within one backend process, complete
  verify-command loops are admitted one at a time per Git common directory, so
  a shared checkout and its linked worktrees cannot launch overlapping full
  builds or test suites. Admission and host-load waits are cancellable and do
  not consume the per-command execution timeout.
- `post-dossier-maintenance` is a mandatory deterministic row in the standard
  and UI pipelines. Cards without a Dossier reference record `NotApplicable`.
  Referenced cards receive `mode-framing-dossier-maintenance.md` in their CORE
  prompt and must append one `<li data-implementation-entry>` carrying the card
  key, delivery date, slice name, and compact shipped summary. Auto Review
  compares the first touching commit's parent with the delivered revision. The
  bytes outside the bounded implementation log and the complete prior log must
  remain unchanged. A missing or non-append-only update fails the visible row
  and follows the existing completion-gate reissue or escalation budget before
  build and aspect review continue.
- Dossier implementation work is cut one card per slice. Each
  `workbench.json.implementationTasks` entry carries, or is normalized to, a
  `bounded-slice` acceptance scope. Promotion rejects a single open-ended
  `implement all recommendations` entry. Multi-slice work uses separate entries
  or an Epic parent with one child per slice; see
  [workflow-sized task cutting](../../operations/workflow-sized-task-cutting.md).
- `PipelineHealthService` is the visibility-only sensor for pipeline-wide
  failure modes. `BuildTestGateRunner` reports acquired/completed pairs into
  it, and the service reads the existing append-only `lane_changed` ledgers.
  It never cancels a gate or moves a task. Code-owned conventions are a
  30-minute acquired-without-completed budget, three consecutive matching
  `failure_fingerprint` values on distinct cards, and a one-hour lane drain
  window that alarms when at least two cards have waited for 15 minutes with
  zero exits. Environmental retries of one card count once for the cross-card
  fingerprint sequence. Alarms append as `alert` / `pipeline-health` rows in
  the orchestrator feed; `GET /api/projects/{projectName}/pipeline-health`
  supplies the compact Pipeline page block with the active gate, global
  fingerprint streak, and completed/hour for each observed lane. This is
  sensor and alarm behavior only. Gate termination remains owned by the
  separate post-acquisition watchdog.
- Abort review is contract-bounded: the model returns a verdict, while
  `PostAbortReviewDecider` owns the binding action and rerun budget.
- The lightweight report pipeline is selected from canonical task mode
  `planning` or `research`. It retains deterministic preflight, one core report
  run, primary-report validation, and human-review handoff. It excludes git,
  build, automated tests, Stylelint, code-review aspects, code-quality grading,
  regression radar, Wiki automation, and drift checks. Research additionally
  requires `results/report.html`; the full deliverable and prompt contract is
  the [Research task delivery convention](../../operations/research-deliverables/index.html).
- The concept pipeline is distinct from the report-only pipeline. It runs in an
  isolated worktree, permits a diff only inside one
  `docs/<slug>/` directory, and never merges that task branch.
  Dossier placement publishes `workbench.json` plus `index.html` through the
  managed project-artifact commit boundary. Concept review checks alternatives,
  recommendation, evidence, open decisions, implementation-card source data,
  the own-card `sourceTaskKeys` entry, `status=decision-pending`, and the dossier
  path in both `results/deliverables.md` and `status.md`. New scaffolds use the
  embedded canonical v2 article template and store `pattern: concept`; callers
  may request `ui`, while readers tolerate missing or unknown values as
  `concept`. The template includes the canonical append-only Implementation
  section and log markers. Its implementation convention requires one
  `implementationTasks` entry per independently reviewable slice, never one
  open-ended all-recommendations entry. The concept pipeline deliberately does not run
  build, test, code aspects, or integration.
  A complete Dossier moves to `5-human-review` with a durable
  `concept-sight-review` marker. `DONE` and `NEEDS_INPUT` both count as
  successful delivery at this gate. Sight-review acceptance completes the
  source card; `POST /api/tasks/{id}/promote-concept` additionally creates the
  selected coding cards from the published document.
- A `Deferred` step is fully implemented but is not executed by the ordinary
  local post-bracket. It is distinct from a `Stub`: a stub has no implementation
  and renders "planned", while a deferred step renders "pending" until a named
  trigger runs it. For `post-merge-into-develop`, a green fenced Remote delivery
  triggers the common runner before Human Review. A failed immediate attempt
  remains visible on the card. Human acceptance does not reset or run this row;
  it completes only a Git-proven integrated delivery. The
  `AcceptedIntegrationWorker` and its backstop may finish only a legacy
  transaction that already has the durable `integrating` phase. A
  delivery first receives a normal `--no-ff` merge. This best case preserves
  every original delivery SHA and adds only the integration merge commit. A
  direct conflict receives one mechanical three-way `ort` merge with recorded
  `rerere` resolution, which has the same SHA-preserving shape. This ordering
  treats attribution as a product value: Grades, Evidence, Records, and review
  subjects refer to exact delivery SHAs (AGT-2562, AGT-2624). Only if both merge
  paths fail may a disposable-worktree rebase run, and it may continue only when
  delivery commit cardinality is unchanged and the old-to-new SHA map is
  one-to-one. A clean replay records `merged-after-rebase` and continues
  through the ordinary gate. A conflict or ambiguous mapping records
  `agent-round-required`; the Remote integration coordinator persists a Steer
  intent, supersedes the current delivery generation, moves the card to the
  front of Ready, and writes `Automatically started a new agent round to
  preserve unambiguous delivery SHA attribution.` to the timeline. This loop is
  limited to one automatic round per operator-owned review epoch. Repetition
  reaches Human Review with the failed step and conflicted files visible. Every
  failed attempt leaves the integration working tree clean. Once
  merge/gate/rollback starts, host cancellation
  does not interrupt that consistency boundary. `/healthz/drain` reports
  `gate-busy` while the boundary is active so the external stable restart
  watcher can wait for a bounded drain window. The paired
  `post-merge-into-develop-push` step (AGT-1999) pushes the integration branch to
  `origin` after a successful merge; it is offloaded off the request path and
  never force-pushes. A push
  failure is a visible step outcome (`environmental` after the AGT-1944 retry
  budget is spent, or `remote-rejected` on a diverged remote) rather than a
  silent drop. The optional AGT-2009 counterpart - auto-cleanup of merged
  `task/*`/`refs/backups/*` refs right after a successful merge step - is
  intentionally **not** wired into the pipeline; merged-ref removal is an
  operator-triggered action only (Project Hub Git-Management). See
  `docs/concepts/task-integration-and-merge-workflow.md` §"Branch cleanup"
  for the dry-run/execute contract and the AGT-1945 guard it would reuse.
- `post-task-spawner` (AGT-2028) is an opt-in `StepKind.Orchestrator` post-step,
  `DefaultEnabled = false` and additionally gated on a `ProjectSettings.TaskSpawner`
  target - a project must both enable the step (`PipelineSteps["post-task-spawner"]`)
  and configure a target project before it fires. It runs in the reporting bracket
  (after the aspects, before the pipeline `Complete` mark) and is reporting-only:
  it NEVER changes the source task's lane decision. The relevance + prompt-generation
  model is quality-first (the live-discovered Codex flagship at its top effort via
  `TaskSpawnerModelSelector`, layered under the per-project step override), while the
  spawned card is left to the target project's default model. It is conservative and
  spam-safe by three guards: a run whose aspects `Block` does not spawn (it is about
  to be reissued); an unparseable / not-relevant / prompt-less verdict spawns nothing;
  and the per-source `.metadata/spawned-tasks.jsonl` ledger caps spawns at
  `MaxPerSourceTask` (default 1) so the reissue loop can never double-spawn. Spawn
  creates a `references.relatedTo` edge to the source (a non-blocking reference, NOT a
  `dependsOn` wait - the separate Task-Dependencies feature turns references into
  waits), records a `task_spawned` timeline entry + a `needs-follow-up-task`
  post-processing outcome on the source, and writes the card through the bounded
  `TaskMutationService.CreateJob` path (never a hand-written folder).
- `post-agents-wiki-sync` (AGT-1782) is an opt-in `StepKind.Tool` post-step,
  `DefaultEnabled = false`, deterministic (no model), and reporting-only: it NEVER
  changes the task lane decision. It depends on the core run (not the aspect
  verdicts) and sits with the sibling wiki producers, before the final decision. It
  writes only under `docs/concepts/designated-topics/` plus, when self-healing
  a missing pointer, a single managed block appended to the project's `AGENTS.md`;
  it never edits a hand-maintained concept page in place (those HTML/Markdown pages
  are human-owned), so the machine-maintained current-state block lives in the
  per-topic `<slug>.md` page referenced by a validated pointer. It is
  self-provisioning (seeds an empty `registry.json` an operator fills in) and
  idempotent (a re-run on the same task refreshes timestamps without duplicating a
  progress row; an unmatched task still validates pointers and regenerates the
  index). A missing concept page is surfaced as a visible dead-pointer finding in
  the generated index's "Pointer health" section and the step reason, never
  silently dropped.
- Pipeline history is per run. Re-opened tasks append a new attempt and keep
  earlier attempts addressable.
- Raw step-call prompts are captured once, at central dispatch, into
  `.metadata/prompts.jsonl` ("Rohdaten komplett, Herleitung als Lesemodell").
  The capture happens BEFORE the inner CLI call so a timed-out / failed step
  still leaves its prompt; it is best-effort and must never propagate an IO
  failure into the run. Only one-shot step-calls that set both `JobFolderPath`
  and `StepId` are recorded; the main run and its follow-ups are deliberately
  excluded (already in `prompt.md` / chat) so there is no double bookkeeping.
  The UI derives, never re-stores: it reads `GET /step-prompts` rather than
  writing a second copy.
- If a new step emits a disk or wire shape, add or update a schema and the
  corresponding fixture tests.

## Verification

- Catalogue changes need `PipelineCatalogueTests` and any step-specific test
  that pins display names, ordering, run mode, and enabled defaults.
- Step condition, model, or order changes need `ProjectSettingsServiceTests`,
  `PipelineStepConditionTests`, and `PipelineStepModelDefaultsTests` coverage.
- Review and abort-review changes need `ReviewDecisionOrchestrator*Tests`,
  `PostAbortReviewDeciderTests`, and `PostAbortReviewStepServiceTests`.
- Quality-grade step changes need `CodeReviewStepServiceTests` (grade parsing,
  tagging, MD render), `CodeReviewGradeModelSelectorTests` (live Codex flagship
  default vs bounded aspect model), `CodeReviewGradeParsingTests` (sentinel
  grammar), and `ReviewDecisionOrchestratorGradeStepTests` (end-to-end: the step
  executes on normal, red build-gate, and aspect-infrastructure paths; invokes the
  Codex flagship; records runtime errors as `Failed`; and stamps only authoritative
  `code-review:grade-*` tags). `PipelineExecutionRestartTests` pins completed-row
  terminalization plus deferred/stub preservation and legacy-read projection.
- Raw step-prompt capture changes need `StepPromptLogTests` (writer/reader
  round-trip with provenance, dedup for main-run shape, capture-before-failure)
  and the `overview-pane.component.spec.ts` step-prompt read-model assertion.
- Agents/wiki-sync changes need `AgentsWikiSyncPostStepRunnerTests` (registry
  seed, tag / path matching, per-topic progress dedup, dead-pointer finding, and
  the AGENTS.md pointer verify / self-heal) plus the `PipelineCatalogueTests`
  step-shape pin (opt-in Tool step, after wiki-learnings and before the decision
  in the standard pipeline, omitted from the lightweight report pipeline).
- Task-spawner changes need `TaskSpawnerPostStepTests` (relevance sentinel parse
  yes/no/unparseable, dedup-ledger budget + same-target block, best-available-model
  default, and the end-to-end runner writing the follow-up card into a target
  project's flat store with a `relatedTo` back-reference) plus the
  `PipelineCatalogueTests` step-shape pin (opt-in Orchestrator step, after aspects
  and before the decision in the standard pipeline, omitted from the lightweight
  report pipeline).
- Frontend pipeline rendering changes need Playwright or component coverage plus
  screenshots when the user-facing view changes.
- Pipeline health changes need `PipelineHealthNightReplayTests`, the
  `pipeline-health-block` component spec, and the mocked night-alarm screenshot
  in `pipeline-page-evidence.spec.ts`.
