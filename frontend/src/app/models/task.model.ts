export type CliType = 'claude' | 'codex' | 'gemini';
export const CLI_TYPES: CliType[] = ['claude', 'codex', 'gemini'];

/**
 * Single source of truth for lane / task-state keys. Mirrors backend
 * `TaskStates` (src/AgentTaskboard.Shared/Models/TaskModels.cs) one-for-one.
 * Every lane literal in frontend prod code routes through this constant so a
 * future lane rename is a two-place value change (here + backend `TaskStates`)
 * plus a data migration, not a 27-file string hunt.
 */
export const TaskState = {
  Backlog: '0-backlog',
  Preparation: '1-preparation',
  OrchestratorPrep: '1a-orchestrator-prep',
  Ready: '2-ready',
  Progress: '3-progress',
  FailedPickup: '3a-failed-pickup',
  CodeNotComplete: '3b-code-not-complete',
  AutoReview: '4-auto-review',
  Escalated: '5e-escalated',
  HumanReview: '5-human-review',
  Completed: '6-completed',
  Archive: '7-archive',
} as const;

/** Union of the canonical lane-key string literals. */
export type TaskStateKey = (typeof TaskState)[keyof typeof TaskState];

/** All canonical lane keys, in board order. */
export const ALL_TASK_STATES: readonly TaskStateKey[] = Object.values(TaskState);

/** One requested state transition in an asynchronous batch-move job. */
export interface BatchMoveItemInput {
  jobId: string;
  watchPath: string;
  targetState: string;
}

/** Final outcome for one batch item, available as soon as that item finishes. */
export interface BatchMoveJobItemResult {
  index: number;
  jobId: string;
  status: string;
  message: string | null;
  durationMs: number;
}

/** Server-side timings for diagnosing filesystem, scanner, lock, and Git cost. */
export interface BatchMoveJobMetrics {
  totalDurationMs: number;
  itemMoveDurationMs: number;
  laneLockAcquisitions: number;
  laneLockWaitMs: number;
  laneLockHeldMs: number;
  scannerInvalidations: number;
  scannerRefreshes: number;
  scannerRefreshMs: number;
  gitProcesses: number;
  gitProcessMs: number;
}

export type BatchMoveJobStatus = 'queued' | 'running' | 'completed' | 'failed';

/** Progress snapshot returned by the asynchronous batch-move endpoints. */
export interface BatchMoveJobResponse {
  id: string;
  status: BatchMoveJobStatus;
  total: number;
  completed: number;
  succeeded: number;
  failed: number;
  results: BatchMoveJobItemResult[];
  metrics: BatchMoveJobMetrics;
  message: string | null;
  createdAt: string;
  startedAt: string | null;
  finishedAt: string | null;
}

// Cycle 9i: this file is the canonical "shared kernel" — TaskInfo,
// TaskDetail, GroupedJobs, CliExecution, etc. Feature-specific types
// (git, tokens, orchestrator, screenshots, claude, run-timeline,
// session-events, project-chat, roadmap, quota, cli, project-token-usage)
// live under their own `features/X/models/` and are accessed via the
// feature barrel. The two `import type` lines below let TaskInfo's
// own field types reference feature-owned shapes without copying them.
import type { TaskCommitInfo, TaskProvenanceRecord, TaskMergeSignal, TaskIntegrationStatus } from '../features/git';
import type { TaskTokenSummary } from '../features/tokens';
import type { OrchestratorLogEntry, OrchestratorSession } from '../features/orchestrator';

/** One row in `logs/session-events.jsonl` for a job. */
// (SessionEvent + SessionEventsResponse now in features/session-events/models; re-exported below)

/**
 * One CLI invocation between two user inputs - the unit of conversation
 * the protocol-pane run timeline renders. Backed by RunRecord on the
 * backend (see backend/Services/Runner/RunTimeline.cs). lineStart /
 * lineEnd are 1-based indices into cli-output.log so the drill-down
 * activity-log filter does not have to re-derive the boundaries.
 */
// (Run timeline / commits / files / diff now in features/run-timeline/models; re-exported below)

// (GitProjectSummary, GitHygieneStatus, TaskHygieneContext now in features/git/models/git.model.ts; re-exported above)

/** Card kind: `epic` is a container for sub-tasks; `task` is an ordinary card. */
export type TaskKind = 'task' | 'epic';

/**
 * Task execution mode. Mirrors backend `TaskModes`. `coding` is the default
 * read-write mode; `planning` and `research` produce read-only reports;
 * `concept` authors one repository dossier; and `research` permits web access
 * by default.
 */
export type TaskMode = 'coding' | 'planning' | 'research' | 'concept';

/**
 * F34 — structured cross-references between tasks, keyed by F33 stable keys
 * (e.g. `ATP-19`). Mirrors backend `TaskReferences`. Four relation kinds:
 * `dependsOn` (target must be complete before this is workable; an object edge
 * can additionally require explicit release),
 * `relatedTo` (thematic, non-blocking), `blockedBy` (currently blocked),
 * `supersedes` (this task replaces an obsolete target).
 */
export interface TaskDependencyReference {
  key: string;
  releaseGate?: boolean;
}

/** Legacy edges stay strings; only release-gated edges need the object shape. */
export type TaskDependency = string | TaskDependencyReference;

export function taskDependencyKey(dependency: TaskDependency): string {
  return typeof dependency === 'string' ? dependency : dependency.key;
}

export function taskDependencyRequiresRelease(dependency: TaskDependency): boolean {
  return typeof dependency !== 'string' && dependency.releaseGate === true;
}

export interface TaskReferences {
  dependsOn: TaskDependency[];
  relatedTo: string[];
  blockedBy: string[];
  supersedes: string[];
  /** Stable project-scoped document reference keys. */
  workbenches?: string[];
}

export interface RelatedWikiPage {
  relPath: string;
  title: string;
  linkedAt: string;
  source: 'auto' | 'manual';
  exists?: boolean | null;
}

/** Task and document relation kinds, in display order. */
export type TaskReferenceKind = 'dependsOn' | 'relatedTo' | 'blockedBy' | 'supersedes' | 'workbenches';
export const TASK_REFERENCE_KINDS: TaskReferenceKind[] = [
  'dependsOn',
  'relatedTo',
  'blockedBy',
  'supersedes',
  'workbenches',
];

/**
 * One incoming reference returned by `GET /api/tasks/{id}/dependents`.
 * Mirrors backend `TaskReferenceLink`: a task (`sourceJobId` / `sourceKey`)
 * points at the queried key via `kind`. Carries enough of the source task to
 * render a chip and route to it without a second lookup.
 */
export interface TaskReferenceLink {
  sourceKey: string | null;
  sourceJobId: string;
  sourceTitle: string;
  sourceState: string;
  sourceWatchPath: string;
  kind: TaskReferenceKind | string;
}

/**
 * AGT-2029: one resolved (or unresolved) waits-on dependency. Mirrors backend
 * `WaitsOnItem`. Carries enough of the target task for the card chip to render
 * its state and route to it - including targets in lanes the board snapshot
 * omits (e.g. archived), which is why the backend resolves this server-side.
 */
export interface WaitsOnItem {
  key: string;
  resolved: boolean;
  fulfilled: boolean;
  releaseGate?: boolean;
  targetReleased?: boolean;
  waitingForRelease?: boolean;
  targetJobId?: string | null;
  targetTitle?: string | null;
  targetState?: string | null;
  targetWatchPath?: string | null;
}

/**
 * AGT-2029: read-time waits-on status derived from `references.dependsOn`
 * against the whole workspace (all projects, all lanes incl. archive). Mirrors
 * backend `WaitsOnStatus`. Present only on cards that have dependsOn edges;
 * drives the state-aware, navigable dependency chip on the board card.
 */
export interface WaitsOnStatus {
  items: WaitsOnItem[];
  /** At least one dependency is not yet fulfilled (open or unknown). */
  blocked: boolean;
  /** The card sits on a dependsOn cycle - a configuration error. */
  cycleDetected: boolean;
}

/** Server-computed active cards that transitively wait on a human decision. */
export interface TransitiveWaitersStatus {
  keys: string[];
  count: number;
}

/**
 * Response body of `PUT /api/tasks/{id}/references` (AGT-2029). The write now
 * persists even when a referenced key is unknown; those edges come back as
 * `warnings` (the target may be created later) rather than a 400.
 */
export interface SetTaskReferencesResponse {
  references: TaskReferences;
  warnings: { code: string; kind: string; target: string; message: string }[];
}

/**
 * AGT-2069: one follow-up card a planning task spawned (from the AGT-2028 spawn
 * ledger). Mirrors backend `PlanningSpawnRef`. Rendered as a "spawnt: AGT-xxxx"
 * microcard chip on the planning task's detail.
 */
export interface PlanningSpawnRef {
  targetKey?: string | null;
  targetJobId?: string | null;
  targetProject?: string | null;
  reason?: string | null;
  at: string;
}

/**
 * AGT-2069: read-time spawn-visibility + spawn-contract projection for a
 * planning task. Mirrors backend `PlanningSpawnSummary`. Present (non-null) only
 * on `mode === 'planning'` cards; drives the "spawnt: AGT-xxxx" chips, the
 * "no follow-up cards" warning, and the accept-dialog guard against the
 * AGT-1915 trap. `contractSatisfied` is true when a follow-up card exists OR
 * the operator declared "no follow-up intended".
 */
export interface PlanningSpawnSummary {
  spawned: PlanningSpawnRef[];
  spawnedCount: number;
  noFollowUpDeclared: boolean;
  noFollowUpReason?: string | null;
  declaredAt?: string | null;
  contractSatisfied: boolean;
}

/**
 * Interim concept-dossier projection. The path is detected from
 * results/deliverables.md or status.md until references.workbenches provides
 * the structured carrier.
 */
export interface ConceptDossierSummary {
  repoRelativePath?: string | null;
  referenceSource?: string | null;
  noDossierNeeded: boolean;
  noDossierReason?: string | null;
  declaredAt?: string | null;
  contractSatisfied: boolean;
}

export interface TaskInfo {
  id: string;
  taskKey: string;
  key?: string | null;
  displayKey?: string | null;
  title: string;
  state: string;
  /** Explicit content approval used only by dependsOn edges with releaseGate=true. */
  released?: boolean;
  order: number;
  agent: string;
  createdAt: string;
  watchPath: string;
  projectName: string;
  folderPath: string;
  lastActivity: string;
  /** UTC instant when the task entered its current lane. */
  enteredLaneAt?: string | null;
  sessionName: string | null;
  /**
   * Per-job orchestrator token rollup. The kanban card renders a small
   * colour-tiered bubble (2.4k / 850k / 3.1M) when this is non-null and
   * the total is greater than zero, with a hover popover showing the
   * detailed breakdown.
   */
  tokenSummary?: TaskTokenSummary | null;
  model: string | null;
  /** False when model qualification derives the route from task type + policy. */
  modelExplicit?: boolean;
  thinkingLevel?: string | null;
  /** False when the policy supplies the reasoning level together with the model tier. */
  thinkingLevelExplicit?: boolean;
  cliType: CliType | null;
  quotaFallback?: {
    cliType: string;
    model: string | null;
    reason: string | null;
  } | null;
  /** Intentional, bounded wait for a confirmed nearby CLI quota reset. */
  quotaWait?: {
    cliType: string;
    startedAt: string;
    resetAt: string;
    thresholdMinutes: number;
    reason: string;
    scope?: 'quota' | 'provider' | string;
  } | null;
  /**
   * Card kind. `epic` cards are containers for sub-tasks; `task` (the default
   * when omitted) is an ordinary card. See backend `TaskKinds`.
   */
  kind?: TaskKind;
  /**
   * Parent epic id when this card is a sub-task of an epic, else null/absent.
   * Set at create time (way 1), post-hoc via PUT /api/tasks/{id}/epic (way 2),
   * or by an epic's decomposition run (way 3).
   */
  epicId?: string | null;
  /**
   * Execution mode. Mirrors backend `TaskInfo.Mode` (default `coding`).
   * Older payloads may omit it, so callers treat absent as `coding`.
   */
  mode?: TaskMode;
  /** Explicit declaration that this card expects no delivery branch. */
  noBranchExpected?: boolean;
  /** Whether the agent may use the web during this task. Mirrors backend `AllowWebAccess`. */
  allowWebAccess?: boolean;
  useOwnSession: boolean | null;
  /**
   * Per-task context-mode override (T1b / ASS-1742): `'clean'` (isolated
   * per-run CLI home) or `'shared'` (the operator's global CLI state). Absent /
   * null means no task override — the run falls back to the project setting and
   * then the platform default (`clean`). Mirrors backend `TaskInfo.ContextMode`.
   */
  contextMode?: string | null;
  lastUsage: SessionUsage | null;
  execution: CliExecution | null;
  commit: TaskCommitInfo | null;
  /**
   * Ordered chain of commits attributed to this task (oldest -> newest).
   * Tasks regularly produce more than one commit across iterations
   * (continue-mode follow-up, crash-recovery + repair, operator-driven
   * steers). Backwards compat: when only the legacy singular `commit`
   * is on disk, the backend surfaces it here as `[commit]`.
   */
  commits?: TaskCommitInfo[];
  /**
   * Cheap "did any run move HEAD / was an auto-commit stamped" signal from
   * the scanner. Lets the card disambiguate a card with zero `commits[]`:
   * `false` -> analysis-only task, render the explicit "no code changes"
   * badge; `true` -> work landed but the attributed chain is still empty,
   * render the "commit discovery pending" diagnostic instead. Never a
   * count - the commit total derives strictly from `commits[]` (SSOT).
   */
  codeActivityDetected?: boolean;
  /** Saved user intent waiting for the auto-pickup loop. Surfaces in the UI as a ⏳ badge. */
  pendingIntent?: PendingIntent | null;
  /**
   * Auto-mode "stuck loop" snapshot - populated only while the orchestrator is
   * actively answering NEEDS_INPUT for this job. Mirrors backend
   * `AutoLoopSnapshot`. The card shows a "auto-loop N/M" badge so the user
   * can see how much of the budget the orchestrator has spent before the
   * circuit breaker stops it.
   */
  autoLoop?: AutoLoopSnapshot | null;
  /**
   * Live state of the post-completion summary (Haiku) call. Populated only
   * while the summarizer is generating or just finished. The card shows an
   * "auto-reviewing" pill so the user knows the orchestrator is still
   * working on a card that just landed in 4-review.
   */
  summaryState?: TaskSummaryState | null;
  /**
   * Latest categorized runner-outcome issue, derived from logs/cli-output.log.
   * This is read-only observability: it surfaces permission blocks, watchdog
   * timeouts, missing sentinels, and classifier ambiguity without creating a
   * second persistence path beside the existing task log.
   */
  outcomeIssue?: TaskOutcomeIssue | null;
  /**
   * Latest orchestrator-review verdict for this job, sourced from the
   * per-project decision journal. Drives the 4-review kanban swim-lane
   * subdivision (orchestrator-review vs human-review) and the workspace
   * top-banner. Values: `pending`, `reissue`, `escalate`, `accept`. Null
   * when no orchestrator decision exists for this card.
   */
  orchestratorVerdict?: 'pending' | 'reissue' | 'escalate' | 'accept' | null;
  /**
   * Client identity that owns this job. References ClientIdentity.id.
   * Defaults to `local-default` for legacy jobs whose `job.json` predates
   * per-task attribution. The frontend renders a small chip on the card
   * (emoji + colour) so reviewers can see at a glance who is responsible.
   */
  ownerClientId?: string | null;
  /**
   * Optional lifecycle substate. Mirrors backend `TaskInfo.Phase`. Drives
   * the kanban Ready group split (Human Ready vs Intake) and the per-card
   * phase chip. Null means "no explicit phase on disk"; the Ready lane
   * defaults to Human Ready in that case (compatibility contract from
   * docs/concepts/expanded-lifecycle-lanes-plan-2026-05.md).
   *
   * Allowed values for 2-ready: `human-ready`, `intake-running`,
   * `intake-blocked`, `intake-passed`. The 3-progress phase values
   * (`execution-running`, `post-processing-running`, ...) ride on the
   * same field but are owned by the post-processing slice.
   */
  phase?: string | null;
  /** UTC time at which the current lifecycle phase was entered. */
  phaseEnteredAt?: string | null;
  /** Read-only checks projected from lifecycle.json while post-processing runs. */
  postProcessingChecks?: LifecycleCheck[];
  /**
   * Run-Liveness Slice B: when this 3-progress card is waiting on an unanswered
   * steer / NeedsInput question (`phase === 'steer-pending'`), the ISO UTC time
   * the wait started - read from the durable `steer-pending.json` marker. Null
   * otherwise. Drives the card's "Waiting for answer since m:ss" pill so the wait
   * is visible instead of an invisible hang.
   */
  steerPendingSince?: string | null;
  /**
   * Structural classification of the task. One of `bug`, `feature`, or
   * `chore` (default for legacy and technical work). Drives the small chip
   * rendered on the kanban card and the type filter pill in the header.
   * Legacy `user-story` values on disk are normalised to `feature` server-side
   * on read.
   */
  taskType?: string;
  /**
   * Workspace-level tag ids attached to this job. Look up display label /
   * colour in the registry served at GET /api/tags. Unknown ids (entries
   * that were soft-deleted from the registry) render as a faint ghost chip.
   */
  tags?: string[];
  /**
   * F34 cross-references to other tasks by F33 stable key. Always present
   * (backend surfaces an empty instance when absent on disk). Drives the
   * detail-view reference section and the card `waiting on KEY` badge.
   */
  references?: TaskReferences;
  /** Wiki pages accumulated by completion post-processing. Missing targets are retained as ghosts. */
  relatedWikiPages?: RelatedWikiPage[];
  /**
   * AGT-2029: read-time waits-on status derived from `references.dependsOn`
   * against the whole workspace (all projects, all lanes incl. archive). Mirrors
   * backend `TaskInfo.WaitsOn`. Present (non-null) only on cards that carry
   * dependsOn edges; drives the state-aware, clickable dependency chip on the
   * board card. Null/absent means "no dependencies".
   */
  waitsOn?: WaitsOnStatus | null;
  /** Active cards that reach this human-review card through dependsOn edges. */
  transitiveWaiters?: TransitiveWaitersStatus | null;
  /**
   * Append-only commit-provenance record (ASS-1724). Mirrors backend
   * `TaskInfo.Provenance` and ships on every board card so the git-state pill
   * can show *where the work actually lives* (active `task/<id>` worktree vs
   * landed in develop vs sequential main-checkout) from ground truth instead of
   * guessing from the lane. Null on legacy `task.json` that predate the field.
   */
  provenance?: TaskProvenanceRecord | null;

  /**
   * AGT-2046: compact, always-on merge signal (is the work in develop / main?).
   * Mirrors backend `TaskInfo.MergeSignal`; computed batched + cached per repo
   * on the backend and folded onto the board payload so the card can render a
   * two-segment `[develop|main]` indicator without a per-task graph query. Null
   * on cards with no committed/merged anchor yet.
   */
  mergeSignal?: TaskMergeSignal | null;

  /**
   * AGT-2202: honest, git-derived integration verdict for accepted cards
   * (5-human-review / 6-completed / 7-archive): is the work actually in develop?
   * Mirrors backend `TaskInfo.Integration`; one of integrated / pending /
   * conflict-skipped / no-branch. It also projects the actual delivery ref from
   * card truth, including runner/<host>/<KEY> and evidenced task/<slug> refs.
   * Resolves the "Accept != Merge" blind spot by reading attributed-commit
   * membership at the current target HEAD; lane state and remembered merge
   * attempts cannot force membership. Null on cards not in an accepted lane.
   */
  integration?: TaskIntegrationStatus | null;

  /**
   * PUB-1: read-time "publishable to" signal for accepted (6-completed) cards -
   * which publish targets (npm / NuGet / website) this task's merged work
   * touches, so the card / detail renders a "publishable: npm, website" chip.
   * Computed batched per project on the backend by set-membership of the task's
   * mainline anchor against each target's pending set. Null on non-accepted
   * cards and cards whose work touches no derived publish target.
   */
  publishSignal?: TaskPublishSignal | null;

  /** Commit-derived test-run evidence. Never persisted on the card. */
  testEvidence?: TaskTestRunEvidence | null;

  /**
   * ASS-1751: read-time run-activity classification for `3-progress` cards,
   * distinguishing a live run, a failed run waiting out the rapid-crash
   * backoff, and an orphan killed by a backend restart. Present only on
   * Progress-lane tasks; null/absent on every other lane. Pure visibility.
   */
  runActivity?: TaskRunActivity | null;

  /**
   * Current-attempt pipeline liveness projected from existing run/step events
   * and queue membership. Previous attempts are never included.
   */
  liveStatus?: TaskLiveStatus | null;

  /**
   * Set when the task was completed out-of-band (operator chat, external
   * agent, remote host) and reconciled through
   * `POST /api/tasks/{id}/external-completion` instead of a runner run.
   * Mirrors backend `TaskInfo.ExternalCompletion`; null on every task that
   * finished through the normal runner/review path. Drives the
   * "extern erledigt" badge on the card. See
   * docs/concepts/out-of-band-task-completion.md §3.
   */
  externalCompletion?: ExternalCompletionInfo | null;

  /**
   * AGT-2003: runner holding this task's active run lease, folded on by the
   * read overlay only while the task is `3-progress` and a lease is held; null
   * otherwise. Mirrors backend `TaskInfo.Runner`. A remote runner acquires the
   * run lease before it spawns its CLI (a local in-process run holds none), so
   * a non-null value with `isRemote` is the signal the board card uses to show
   * "→ <runner>" next to the CLI badge instead of the quiet local presentation.
   */
  runner?: TaskRunnerInfo | null;

  /** Canonical runtime-backed execution owner, health, and routing context. */
  executionLocation?: TaskExecutionLocation | null;

  /**
   * AGT-2069: read-time spawn-visibility + spawn-contract projection, present
   * (non-null) only on planning-mode cards. Mirrors backend
   * `TaskInfo.PlanningSpawn`. Drives the spawn chips / "no follow-up cards"
   * warning on the planning task's detail and the accept-dialog guard against
   * the AGT-1915 trap. Null on every coding / research / epic card.
   */
  planningSpawn?: PlanningSpawnSummary | null;
  /** Concept dossier link or deliberate no-dossier decision. Null outside concept mode. */
  conceptDossier?: ConceptDossierSummary | null;
}

export interface LifecycleCheck {
  name: string;
  status: 'pending' | 'running' | 'passed' | 'failed' | 'skipped' | string;
  startedAt?: string | null;
  finishedAt?: string | null;
  detail?: string | null;
}

/**
 * Card-renderable projection of the runner that holds a task's active run lease
 * (AGT-2003). Mirrors backend `TaskRunnerInfo`. Sourced from the in-memory
 * run-lease record; `runnerName` + `isRemote` drive the badge, the lease id /
 * fencing token ride along for the tooltip.
 */
export interface TaskRunnerInfo {
  runnerId: string;
  /** Human-facing runner name shown on the badge (e.g. `agent-runner-01`). */
  runnerName: string;
  hostname: string;
  backendName: string;
  /** True when the lease owner is a different runner than this backend — a remote host. */
  isRemote: boolean;
  leaseId: string;
  fencingToken: number;
  /** UTC ISO instant the active lease was acquired. */
  acquiredAt: string;
}

export type TaskExecutionState =
  | 'local-running'
  | 'remote-running'
  | 'remote-disconnected'
  | 'queued-remote'
  | 'recovering'
  | 'no-active-execution';

export interface RemoteDispatchRejection {
  code: string;
  runnerId: string;
  runnerName: string;
  reason: string;
  rejectedAtUtc: string;
}

export interface TaskExecutionLocation {
  state: TaskExecutionState;
  executionKind: 'local' | 'remote' | 'none';
  runnerId?: string | null;
  clientId?: string | null;
  hostDisplayName?: string | null;
  configuredRunnerId?: string | null;
  startedAt?: string | null;
  lastHeartbeat?: string | null;
  lastActivityAt?: string | null;
  processId?: number | null;
  sessionId?: string | null;
  branch?: string | null;
  worktreePath?: string | null;
  connectionState: string;
  leaseState: string;
  trustReason: string;
  historical?: boolean;
  /** Latest remote Runner refusal during the task's current Ready-lane stay. */
  lastRejection?: RemoteDispatchRejection | null;
}

/**
 * Provenance of an out-of-band task completion. Mirrors backend
 * `ExternalCompletionInfo`; the canonical narrative lives in
 * `results/deliverables.md` and the `external_completion` timeline event, this
 * is the small card-renderable summary behind the "extern erledigt" badge.
 */
export interface ExternalCompletionInfo {
  /** Who / which channel completed the task (operator name, agent id, "chat", ...). */
  source: string;
  /** One-line result summary shown in the badge tooltip; may be empty. */
  summary?: string | null;
  /** UTC instant the external completion was recorded (ISO 8601). */
  completedAt: string;
}

export interface TaskOutcomeIssue {
  kind:
    | 'permission-blocked'
    | 'watchdog-timeout'
    | 'tool-router-error'
    | 'no-reply'
    | 'missing-terminal-sentinel'
    | 'classifier-unknown'
    | 'heuristic-done'
    | 'environment-blocker'
    | string;
  label: string;
  severity: 'Info' | 'Warn' | 'High' | string;
  /** Bounded compatibility text for compact consumers. */
  summary: string;
  /** Complete normalized source line, rendered only inside technical details. */
  technicalDetails?: string | null;
  lastSeenAt: string | null;
}

/**
 * One entry returned by GET /api/clients. Keep in sync with the backend
 * `ClientSummary` record.
 */
export interface ClientSummary {
  id: string;
  displayName: string;
  emoji: string | null;
  colour: string | null;
  kind: 'human' | 'agent-instance' | 'external-tool' | 'service' | 'retired';
  registeredAt: string;
  lastSeenAt: string | null;
  tokenBudgetMonthly: number | null;
  notes: string | null;
  defaultCliType?: string | null;
  defaultModel?: string | null;
  defaultThinkingLevel?: string | null;
  runnerGitStatus?: 'ready' | 'ready-no-workflow-scope' | 'read-only' | null;
  runnerGitDetail?: string | null;
  runnerGitCheckedAt?: string | null;
  runnerProjectPreflights?: RunnerProjectPreflight[];
  drainRequestedAt?: string | null;
  retireRequestedAt?: string | null;
  runnerDaemonState?: 'running' | 'read-only' | 'stopped' | null;
  runnerLastClaimAt?: string | null;
  runnerActiveSlots?: number | null;
  runnerAvailableSlots?: number | null;
  /** Central host capacity targets (AGT-2302 / AGT-2376). */
  runnerDesiredMaxParallelism?: number | null;
  runnerTargetLoadPercent?: number | null;
  runnerRampStrategy?: 'conservative' | 'balanced' | 'aggressive' | null;
  runnerCapacityUpdatedAt?: string | null;
  /** Ceiling the live daemon reports as adopted. Telemetry, not policy. */
  runnerEffectiveMaxParallelism?: number | null;
  runnerEffectiveMaxParallelismAppliedAt?: string | null;
  /** Present only for a synthetic row representing an unreadable identity file. */
  identityFileError?: string | null;
  identityFileName?: string | null;
  identityFileModifiedAt?: string | null;
  identityFileSizeBytes?: number | null;
  identityRestoreHint?: string | null;
}

export interface RunnerProjectPreflight {
  projectId: string;
  projectName: string;
  registrationFingerprint: string;
  repositoryUrl: string;
  fetchUrl: string;
  pushUrl: string;
  targetBranch?: string;
  status: 'ready' | 'failed';
  detail: string;
  checkedAt: string;
}

/**
 * Body returned by GET/PUT /api/clients/{id}/defaults. Keep in sync with the
 * backend `ClientDefaultsResponse` record.
 */
export interface ClientDefaultsResponse {
  id: string;
  defaultCliType: string | null;
  defaultModel: string | null;
  defaultThinkingLevel: string | null;
}

/**
 * Per-job orchestrator token rollup. Mirrors backend `TaskTokenSummary`.
 * Surfaced on the kanban card as a colour-tiered "token bubble" with a
 * hover popover that lists per-call rows.
 */
// (TaskTokenSummary, TaskTokenCall now in features/tokens/models/tokens.model.ts; re-exported below)

export interface AutoLoopSnapshot {
  iteration: number;
  maxIterations: number;
  tokensUsed: number;
  maxTokens: number;
  startedAt: string;
  lastAt: string;
  lastQuestion?: string | null;
  lastReply?: string | null;
  lastError?: string | null;
}

// (TaskCommitInfo, TaskCommitDetail now in features/git/models/git.model.ts; re-exported above)

export interface SessionUsage {
  at: string;
  tokens: string | null;
  changes: string | null;
  requests: string | null;
}

// (CliModelInfo, CliModelCatalog, CopilotModelInfo, CopilotModelCatalog,
// CliSessionInfo, CliUsageProjectGroup, CliUsageSection, CliUsageReport
// now in features/cli/models/cli.model.ts; re-exported below.)

export type TaskSummaryStatus = 'none' | 'generating' | 'ready' | 'failed' | 'degraded';

/**
 * How a follow-up sent through the chat box should be interpreted by the
 * runner. Mirrors backend ContinueModes.
 *
 * - continue: next conversation turn, default.
 * - steer:    course correction; the agent overrides its current plan.
 * - extend:   additive extension; backend writes prompt-N.md and the agent
 *             treats the original task plus the new prompt as the full job.
 * - newTask:  new sub-task in the same session; prior context preserved but
 *             the request is new.
 */
export type ContinueMode = 'continue' | 'steer' | 'extend' | 'newTask';

/**
 * Saved user intent waiting for the auto-pickup loop to run. Populated when
 * the user sends a follow-up to a job that is not the project's current
 * active job; the project busy at the time saved this draft on disk.
 * Mirrors backend `PendingIntent`.
 */
export interface PendingIntent {
  version: number;
  mode: ContinueMode;
  prompt: string;
  savedAt: string;
  savedReason: string;
  savedAgainstActiveJobId: string | null;
}

/**
 * Discriminated response for `POST /api/tasks/{id}/continue` and `/start`.
 * `started` means the run is live; `queued` means the project was busy
 * with another job, the user's intent has been saved on the target task,
 * and the target task is now at the top of `2-ready`. The frontend treats
 * `queued` as success-with-info (no modal); the chat carries the
 * orchestrator's `[queued]` line.
 */
export interface ContinueTaskResponse {
  status: 'started' | 'queued';
  execution?: CliExecution | null;
  queued?: ContinueTaskQueuedInfo | null;
}

export interface ContinueTaskQueuedInfo {
  reason: 'project-busy';
  activeJobId?: string | null;
  activeJobTitle?: string | null;
  position: number;
  promotedFromState?: string | null;
}

/**
 * One entry in the orchestrator log feed for a project. Mirrors backend
 * `OrchestratorLogEntry`. Kinds: decision / action / observation /
 * intervention. Topics group entries in the UI feed.
 */
// (OrchestratorLogEntry/TokenUsage/Response now in features/orchestrator/models; re-exported below)

/**
 * Per-project rollup of orchestrator token amounts plus a theoretical
 * API-cost estimate. Mirrors backend `TokenSummary`. Three independent
 * dimensions are surfaced separately by the UI: amounts (real),
 * theoretical API cost (estimate, must carry the disclaimer), and
 * subscription quota (linked from `/api/cli/quota`, not folded here).
 */
// (Token rollups + ad-hoc usage + timeline now in features/tokens/models/tokens.model.ts; re-exported below)

// (ProjectTokenCategory, ProjectTokenUsageSummary, ProjectTokenHeatmapCell,
// ProjectTokenHeatmapJob, ProjectTokenHeatmap, ProjectExpensiveJob,
// ProjectExpensiveJobsResponse, ProjectJobTokenRun, ProjectJobTokenDetail
// now in features/project-token-usage/models; re-exported below.)

/**
 * Mirrors backend `TaskScreenshot`. One screenshot file produced during
 * a job's runs and harvested into `<job>/results/`. The strip in the
 * protocol pane and the workspace visual evidence reel both render
 * arrays of these.
 */
// (TaskScreenshot + TaskScreenshotsResponse + WorkspaceScreenshotsResponse now in features/screenshots/models; re-exported below)

/**
 * Long-lived orchestrator session record. The orchestrator boots one of
 * these per project at app start; subsequent decisions resume the same
 * Claude session via `-r <sessionId>`, so the orchestrator carries
 * project context and prior decisions in its conversation memory.
 * Mirrors backend `OrchestratorSession`.
 */
// (Orchestrator session + chat now in features/orchestrator/models; re-exported below)

// Project chat now renders through the `coding-agent-chat` Composer host in
// features/orchestrator. The former app-local Slice D models/components were
// retired with MC-0a.

// (TokenSummaryByModel now in features/tokens/models/tokens.model.ts; re-exported below)

export interface TaskSummaryState {
  status: TaskSummaryStatus;
  startedAt: string | null;
  finishedAt: string | null;
  errorMessage: string | null;
  bytesWritten: number | null;
  attempt?: number | null;
  maxAttempts?: number | null;
}

/**
 * One entry in the prompt-extension timeline. Backend writes prompt-1.md,
 * prompt-2.md, ... when the user sends a follow-up in Extend mode. The
 * Task Description pane renders these as a blog-style sequence below the
 * original task body.
 */
export interface TaskPromptHistoryEntry {
  index: number;
  fileName: string;
  markdown: string;
  writtenAt: string;
}

/**
 * One entry in the task's title-revision timeline, stored in
 * `title-history.json` in the job folder. Appended by the rename
 * endpoint whenever the title actually changes. Oldest first.
 */
export interface TaskTitleHistoryEntry {
  at: string;
  oldTitle: string;
  newTitle: string;
  source: string;
}

export type PromptEnrichmentStatus = 'enriched' | 'unchanged' | 'fallback-unenriched' | 'blocked';

export interface PromptEnrichmentCandidate {
  id: string;
  title: string;
  source: string;
  signals: string[];
  decision: 'appended' | 'rejected-budget' | 'rejected-project-disabled' | string;
  reason: string;
  estimatedTokens: number;
}

export interface PromptEnrichmentBlock {
  id: string;
  title: string;
  source: string;
  revision: string;
  digestSha256: string;
  tier: string;
  order: number;
  estimatedTokens: number;
  exactContent: string;
}

export interface PromptEnrichmentReport {
  schemaVersion: string;
  enrichmentId: string;
  generatedAtUtc: string;
  status: PromptEnrichmentStatus;
  originalPromptSha256: string;
  enrichedPromptSha256: string;
  policy: {
    id: string;
    version: string;
    projectEnabled: boolean;
    selector: string;
    tokenizer: string;
    tokenBudget: number;
    optionalBlockLimit: number;
    styleGuideSnapshotId?: string | null;
  };
  detectedAreas: string[];
  candidates: PromptEnrichmentCandidate[];
  appendedBlocks: PromptEnrichmentBlock[];
  tokens: {
    tokenizer: string;
    original: number;
    appended: number;
    final: number;
    preprocessingInput: number;
    preprocessingOutput: number;
    preprocessingCacheRead: number;
    preprocessingCacheCreation: number;
  };
  cost: {
    currency: string;
    selectorUsd: number;
    appendedInputUsd?: number | null;
    estimateModel?: string | null;
    unknownReason?: string | null;
  };
  timingMs: number;
  warnings: string[];
  errors: string[];
}

export interface TaskDetail {
  info: TaskInfo;
  promptMarkdown: string | null;
  enrichmentReport?: PromptEnrichmentReport | null;
  promptHistory: TaskPromptHistoryEntry[];
  titleHistory: TaskTitleHistoryEntry[];
  statusMarkdown: string | null;
  statusGeneration?: FileGenerationMeta | null;
  contextUsage: ContextUsageSnapshot | null;
  log: TaskLogEntry[];
  summaryState: TaskSummaryState | null;
  /**
   * Task-level review evidence. Populated from
   * `<job>/results/review-evidence.jsonl`. Findings produced by security
   * audits, code-review passes, task checks, or human notes. Empty when
   * the file is absent. Findings are evidence for review, not blockers:
   * the lane transitions never gate on them. See
   * `docs/system/contracts/filesystem.md` "results/review-evidence.jsonl".
   */
  reviewEvidence: ReviewEvidenceEntry[];
}

export type ReviewEvidenceSource = 'security-audit' | 'code-review' | 'task-check' | 'human-note' | 'other';
export type ReviewEvidenceSeverity = 'info' | 'warn' | 'high';

export interface ReviewEvidenceEntry {
  id: string;
  source: ReviewEvidenceSource;
  severity: ReviewEvidenceSeverity;
  title: string;
  ruleId?: string | null;
  body: string | null;
  createdAt: string;
  runIndex: number | null;
  artifacts: string[];
  fileRefs: string[];
  acknowledged: boolean;
  followupJobId: string | null;
}

export interface ContextUsageSnapshot {
  at: string;
  command: string;
  status: string;
  error: string | null;
  metrics: ContextUsageMetric[];
  sections: ContextUsageSection[];
  notes: string[];
  rawText: string;
}

export interface ContextUsageMetric {
  label: string;
  value: string;
}

export interface ContextUsageSection {
  title: string;
  items: string[];
}

export interface TaskLogEntry {
  timestamp: string;
  event: string;
  detail: string | null;
}

/**
 * Kind classification used by the Files tab (F48). Mirrors
 * `TaskArtifactKind` on the backend; values arrive as camel-case strings
 * because of `JsonStringEnumConverter(JsonNamingPolicy.CamelCase)`.
 */
export type TaskArtifactKind = 'prompt' | 'aspect' | 'codeReview' | 'note' | 'other';

export interface FileGenerationMeta {
  file: string;
  kind: string;
  model?: string | null;
  cli?: string | null;
  tokensIn: number;
  tokensOut: number;
  cacheReadTokens?: number;
  cacheCreationTokens?: number;
  tokensTotal: number;
  startedAt?: string | null;
  endedAt?: string | null;
  durationMs: number;
  runIndex?: number | null;
  stepId?: string | null;
  headShaAfter?: string | null;
}

/**
 * One supported document in the job root surfaced by the Files tab. Markdown,
 * HTML, and structured aspect JSON are listed. The content
 * itself is not embedded — the Files tab fetches it lazily through
 * `GET /api/tasks/{id}/files/{fileName}` lazily for the Files-tab surface.
 */
export interface TaskArtifact {
  name: string;
  sizeBytes: number;
  mtime: string;
  kind: TaskArtifactKind;
  /** Set when `kind === 'aspect'`; e.g. `code-quality` for `aspect-code-quality.md`. */
  aspectName?: string | null;
  generation?: FileGenerationMeta | null;
}

export interface TaskArtifactsResponse {
  jobId: string;
  files: TaskArtifact[];
}

export type TaskFileSourceScope = 'auto' | 'workspace' | 'code';

export interface TaskFileVersionProvenance {
  source: TaskFileSourceScope | string;
  path: string;
  steps?: string | null;
  generation?: FileGenerationMeta | null;
}

export interface TaskFileHistoryEntry {
  sha: string;
  at?: string | null;
  runIndex?: number | null;
  verdict?: string | null;
  message: string;
  author: string;
  provenance: TaskFileVersionProvenance;
}

export interface GroupedJobs {
  /** Triage staging area; default landing for new jobs, never auto-picked. */
  backlog: TaskInfo[];
  preparation: TaskInfo[];
  /**
   * Retired 1a-orchestrator-prep lane. Orchestrator prep now runs in-place on
   * 1-preparation as the optional `pre-orchestrator-prep` pipeline step (see
   * PipelineCatalogue); the board no longer renders this lane. The field stays
   * so the frontend keeps parsing the grouped payload (same retired-lane
   * pattern as `failedPickup`), and the backend boot-migrates any stray 1a
   * cards back to 1-preparation. Always empty after a clean boot.
   */
  orchestratorPrep: TaskInfo[];
  ready: TaskInfo[];
  progress: TaskInfo[];
  /**
   * ADR-0051 drain-era plumbing: the retired 3a-failed-pickup lane. No live
   * path populates it and the board no longer renders it; the field stays so
   * the frontend keeps parsing the grouped payload while the backend boot
   * drain empties any historical folders. Always empty after a clean boot.
   */
  failedPickup: TaskInfo[];
  /**
   * Park lane for tasks that exhausted their auto-pickup retry budget without
   * reaching review (3b-code-not-complete). Hide-when-empty. The runner moves
   * an offender here and keeps auto-mode running; the project only flips to
   * manual once the systemic "3x3" pattern trips.
   */
  codeNotComplete: TaskInfo[];
  /** ADR-0025 lane: orchestrator's review pass (4-auto-review). */
  autoReview: TaskInfo[];
  /** Acceptance lane for finished deliveries backed by evidence (5-human-review). */
  humanReview: TaskInfo[];
  /** Decision lane: true escalations that need operator input (5e-escalated). */
  escalated: TaskInfo[];
  /** Legacy alias for pre-ADR-0025 clients; equal to `autoReview`. */
  review: TaskInfo[];
  completed: TaskInfo[];
  archive: TaskInfo[];
}

/**
 * ASS-1727 — one row in the paged Archive read endpoint
 * (`GET /api/tasks/archive`). The board `grouped.archive` lane is
 * intentionally empty (the cache-backed board scan excludes the terminal
 * lane), so the Archive view hydrates from this slim projection instead of
 * the full `TaskInfo`. Mirrors backend `ArchivedTaskInfo`.
 */
export interface ArchivedTaskInfo {
  id: string;
  taskKey: string;
  key?: string | null;
  title: string;
  state: string;
  projectName: string;
  watchPath: string;
  enteredLaneAt: string;
  lastActivity: string;
  commitCount: number;
  codeActivityDetected: boolean;
  taskType: string;
  cliType?: string | null;
  agent: string;
}

/**
 * ASS-1727 — paged envelope for `GET /api/tasks/archive`. `total` is the
 * full unpaged count (drives "load more" / empty-state); `items` is the
 * newest-first slice for the requested `offset`/`limit`. Mirrors backend
 * `ArchivedTasksResponse`.
 */
export interface ArchivedTasksResponse {
  items: ArchivedTaskInfo[];
  total: number;
  offset: number;
  limit: number;
}

export interface CreateTaskRequest {
  id?: string;
  title: string;
  order?: number;
  agent: string;
  watchPath: string;
  promptMarkdown?: string;
  targetState?: string;
  cliType?: CliType;
  model?: string;
  thinkingLevel?: string;
  modelExplicit?: boolean;
  thinkingLevelExplicit?: boolean;
  /** One of `bug`, `feature`, `chore`. Defaults to `chore` server-side. */
  taskType?: string;
  /** Workspace tag ids to attach on create. */
  tags?: string[];
  /** Card kind: `task` (default) or `epic`. */
  kind?: TaskKind;
  /** Parent epic id (assignment way 1: created as a sub-task of this epic). */
  epicId?: string;
  /** Execution mode. Defaults to `coding` server-side. */
  mode?: TaskMode;
  /** Explicitly declare that this task expects no delivery branch. */
  noBranchExpected?: boolean;
  /** Web access. When omitted, defaults by mode (research = on, else off). */
  allowWebAccess?: boolean;
  /** Ownership-routing input. The backend resolves and validates the destination. */
  routing?: ComponentRoutingRequest;
  requestedTaskPrefix?: string;
}

/**
 * Payload from GET /api/tasks/{id}/promote-to-coding: a pre-filled coding-task
 * draft derived from a finished planning task. The frontend seeds the existing
 * create-task modal with these fields and re-uploads `attachments` byte-for-byte
 * into the new task. See docs/concepts/planning-research-task-kinds-2026-05.md.
 */
export interface PromoteToCodingResponse {
  title: string;
  promptMarkdown: string;
  mode: TaskMode;
  targetState: string;
  watchPath: string;
  projectName: string;
  attachments: PromoteAttachmentRef[];
}

/** One copyable image attachment from a promoted planning task. */
export interface PromoteAttachmentRef {
  fileName: string;
  /** Source folder on the planning task: `results` or `attachments`. */
  source: string;
  /** Relative API URL serving the image bytes. */
  url: string;
}

export interface ConceptImplementationTask {
  title: string;
  promptMarkdown: string;
}

export interface ConceptSourceDocument {
  repoRelativePath: string;
  title: string;
}

/** Validated implementation-card proposals from a published concept Dossier. */
export interface PromoteConceptResponse {
  source: ConceptSourceDocument;
  items: ConceptImplementationTask[];
  mode: TaskMode;
  targetState: string;
  watchPath: string;
  projectName: string;
}

export interface PromotedConceptTask {
  jobId: string;
  taskKey?: string | null;
  title: string;
}

export interface PromoteConceptTasksResponse {
  source: ConceptSourceDocument;
  created: PromotedConceptTask[];
}

/**
 * One epic + its live sub-task rollup, from GET /api/epics. Progress is derived
 * from the sub-tasks' lanes server-side, so it always matches the board.
 */
export interface EpicRollup {
  id: string;
  /** Stable human key of the epic (e.g. "ASS-597"); null on epics minted before keys existed. */
  key?: string | null;
  title: string;
  projectName: string;
  watchPath: string;
  state: string;
  subTaskTotal: number;
  completed: number;
  inProgress: number;
  open: number;
  /** Latest lane-entry timestamp among all members once the epic is complete. */
  completedAt?: string | null;
  byState: Record<string, number>;
  subTasks: EpicSubTaskRef[];
}

export interface EpicSubTaskRef {
  id: string;
  title: string;
  state: string;
  order: number;
  orchestratorVerdict?: 'pending' | 'reissue' | 'escalate' | 'accept' | null;
}

/** Body for POST /api/epics/{id}/sub-tasks (assignment way 3, deterministic half). */
export interface CreateEpicSubTasksRequest {
  subTasks: EpicSubTaskSpec[];
}

export interface EpicSubTaskSpec {
  title: string;
  promptMarkdown?: string;
  cliType?: CliType;
  model?: string;
  thinkingLevel?: string;
}

/**
 * Workspace-level tag registry entry served by GET /api/tags. Drives the
 * label + colour for tag chips on cards and in the filter bar.
 */
export interface TagRegistryEntry {
  id: string;
  label: string;
  color: string;
  description: string;
}

export interface TaskOrderItem {
  jobId: string;
  watchPath: string;
}

export interface WatchPathEntry {
  name: string;
  path: string;
  rootPath: string;
}

/** Mirrors backend `ProjectUrlStartRule`: how to build/start a URL's server. */
export interface ProjectUrlStartRule {
  command: string;
  cwd: string | null;
  port: number | null;
  healthUrl?: string | null;
  /** Console-silence window. Existing persisted values retain this field. */
  readinessTimeoutSeconds?: number;
  /** Absolute startup ceiling even while console output remains active. */
  startupTimeoutSeconds?: number;
  /** `manual` | `package-json` | `readme`. */
  source: string;
}

/** Snapshot of a backend-owned dev-server process for a project URL. */
export interface ProjectUrlProcessSnapshot {
  started: boolean;
  projectId: string;
  urlId: string;
  command: string;
  cwd: string;
  state: 'starting' | 'running' | 'exited' | 'stopped' | 'failed';
  processId: number | null;
  startedAtUtc: string;
  finishedAtUtc: string | null;
  exitCode: number | null;
  output: string[];
}

/** Mirrors backend `ProjectUrlRecord`: one watchable URL on a project. */
export interface RegistryProjectUrl {
  id: string;
  label: string;
  url: string;
  sortOrder: number;
  startRule: ProjectUrlStartRule | null;
}

export interface ComponentOwnershipMapping {
  id: string;
  observedSurfaces: string[];
  component: string;
  packageOrModule: string | null;
  primaryProjectId: string;
  repository: string | null;
  consumerProjectIds: string[];
  integrationHosts: string[];
  releaseArtifact: string | null;
  versioningMechanism: string | null;
  deploymentSteps: string[];
  environments: string[];
  allowedTicketPrefix: string;
  evidence: string[];
  confidence: number;
  unresolvedAlternatives: string[];
  version: number;
  updatedAt: string;
  updatedBy: string;
}

export interface ComponentRoutingRequest {
  observedSurface?: string | null;
  component?: string | null;
  navigationProjectId?: string | null;
}

export interface ComponentRoutingResolution {
  observedSurface: string | null;
  component: string | null;
  packageOrModule: string | null;
  navigationProject: { id: string; shortCode: string; displayName: string } | null;
  primaryProject: { id: string; shortCode: string; displayName: string } | null;
  primaryProjectId?: string | null;
  projectShortCode?: string | null;
  repository: string | null;
  consumerProjects: { id: string; shortCode: string; displayName: string }[];
  integrationHosts: string[];
  releaseArtifact: string | null;
  versioningMechanism: string | null;
  deploymentSteps: string[];
  environments: string[];
  allowedTicketPrefix: string | null;
  storageProjectId: string | null;
  evidence: string[];
  confidence: number;
  routingConfidence?: number;
  unresolvedAlternatives: string[];
  requiresQuestion: boolean;
  questionReason: string | null;
  preview: string;
  mappingId: string | null;
  mappingVersion: number | null;
}

/** Mirrors backend `ProjectUrlSuggestion` from `GET .../url-suggestions`. */
export interface ProjectUrlSuggestion {
  label: string;
  url: string | null;
  command: string;
  cwd: string | null;
  port: number | null;
  /** `package-json` | `angular-json` | `readme`. */
  source: string;
}

/** Compatibility name retained for existing start-only consumers. */
export type ProjectUrlStartResponse = ProjectUrlProcessSnapshot;

/** AGT-2180 — stable classification vocabulary for URL Preview diagnostics. */
export type ProjectUrlDiagnosisClass =
  | 'not-started' | 'starting' | 'command-unavailable' | 'invalid-cwd'
  | 'process-exited' | 'port-in-use' | 'port-never-opened' | 'timeout' | 'http-error-response'
  | 'content-not-renderable' | 'invalid-configuration' | 'running';

/** AGT-2180 — bounded, redacted evidence snapshot behind the offline card. */
export interface ProjectUrlDiagnostic {
  classification: ProjectUrlDiagnosisClass;
  summary: string;
  recommendedAction: string;
  command: string | null;
  cwd: string | null;
  url: string | null;
  configuredPort: number | null;
  processCreated: boolean;
  exitCode: number | null;
  stdoutTail: string;
  stderrTail: string;
  timedOut: boolean;
  portReachable: boolean;
  httpStatus: number | null;
  contentReady: boolean;
  startupFailureReason?: 'process-exit' | 'port-in-use' | 'silence-timeout' | 'startup-limit' | null;
  occupyingProcessId?: number | null;
  occupyingProcessName?: string | null;
  /** Browser embedding evidence when response headers or the iframe can decide it. */
  iframeReady?: boolean | null;
  /** Bounded blocking X-Frame-Options/CSP evidence, when present. */
  framePolicy?: string | null;
  checkedAt: string;
}


/**
 * F45a / ADR-0042 — flat project summary returned by `GET /api/projects`
 * and embedded under `WorkspaceListItem.projects`. Mirrors backend
 * `ProjectSummary`.
 */
export interface RegistryProjectSummary {
  sourceType: ProjectSourceType;
  id: string;
  displayName: string;
  shortCode: string;
  workspaceId: string;
  color: string | null;
  cliDefault: string | null;
  modelDefault: string | null;
  sortOrder: number;
  storageLocation: string;
  repositoryPath: string | null;
  rootPath: string | null;
  /** Well-known repository URL (`urls[id=repo]`) projected for project basics editing. */
  repositoryUrl: string | null;
  /** Optional read-only git ref supplying the complete project Wiki. */
  wikiSourceBranch?: string | null;
  /** Configured watchable URLs, ordered; empty for most projects. */
  urls: RegistryProjectUrl[];
  ownershipMappings?: ComponentOwnershipMapping[];
  archived: boolean;
  createdAt: string;
}

export interface CreateRegistryProjectRequest {
  workspaceId: string;
  displayName: string;
  shortCode?: string;
  cliDefault?: CliType;
  modelDefault?: string | null;
  color?: string | null;
  /**
   * Optional CLI working directory. Without this, a project has no
   * auto-pickup runner until someone sets it later via project settings
   * (or hand-edits the gitignored appsettings.Local.json WatchPaths entry).
   */
  rootPath?: string;
  repositoryPath?: string;
  repositoryUrl?: string;
  executionRunner?: string;
}

export type ProjectSourceType = 'local-folder';

/**
 * F45a / ADR-0042 — workspace listing entry returned by `GET /api/workspaces`.
 * Mirrors backend `WorkspaceListItem`; embeds the active (non-archived)
 * projects so the sidebar can render in a single round-trip.
 */
export interface RegistryWorkspaceListItem {
  id: string;
  displayName: string;
  sortOrder: number;
  isDefault: boolean;
  color: string | null;
  createdAt: string;
  projects: RegistryProjectSummary[];
}

export interface CliExecution {
  jobId: string;
  taskKey: string;
  processId: number;
  startedAt: string;
  status: string;
  exitCode: number | null;
  durationSeconds: number | null;
  model: string | null;
  thinkingLevel?: string | null;
  runOutcome?: string | null;
}

/**
 * ASS-1751: the four ways a `3-progress` card can look "untouched", as
 * classified by the backend at read time:
 * - `active` — a run process is alive and occupies a parallelism slot.
 * - `failed-backoff` — the last run failed and a rapid-crash backoff is still
 *   in effect; the task is waiting for re-pickup (carries `backoffUntil`).
 * - `failed-idle` — the last run failed (or a fail-without-progress streak is
 *   recorded) but no backoff is active and nothing is running.
 * - `no-active-run` — no live run, no backoff, no recorded failure; e.g. an
 *   orphan after a backend restart awaiting re-pickup.
 */
export type TaskRunActivityKind = 'active' | 'failed-backoff' | 'failed-idle' | 'no-active-run';

/**
 * Read-time visibility projection for a `3-progress` task (ASS-1751). Purely
 * informational — it carries no behavior; the kanban card and the task-detail
 * header render a small, quiet status pill from it. Present only on
 * Progress-lane tasks; absent (null/undefined) on every other lane.
 */
export interface TaskRunActivity {
  kind: TaskRunActivityKind;
  /** OS process id of the live run; set only when `kind === 'active'`. */
  processId?: number | null;
  /** UTC ISO instant the rapid-crash backoff expires; set only when `kind === 'failed-backoff'`. */
  backoffUntil?: string | null;
  /** Consecutive fail-without-progress attempts recorded for this task (0 when none). */
  attempt: number;
  /** One-line last-error summary mirrored from the outcome issue; null when unknown. */
  lastError?: string | null;
}

export interface TaskLiveStep {
  stepId: string;
  displayName: string;
  kind: string;
  startedAt?: string | null;
  model?: string | null;
  cliType?: string | null;
}

export interface TaskLiveStepPreview {
  stepId: string;
  displayName: string;
}

export interface TaskLiveQueue {
  kind: 'runner' | 'review' | string;
  position: number;
}

export interface TaskLiveStatus {
  attempt: number;
  activeStep?: TaskLiveStep | null;
  nextSteps: TaskLiveStepPreview[];
  queue?: TaskLiveQueue | null;
  latestEventAt?: string | null;
}

export interface CliOutputLine {
  timestamp: string;
  stream: string;
  text: string;
  /**
   * Set by the host conversation-projection guard
   * (`features/task-detail/components/conversation-projection.ts`) when `text`
   * was redacted to the `[internal event]` marker because the original line was
   * a raw stream-json transport frame. Holds the original raw JSON so Trace /
   * Verbose-Debug can disclose it on demand; the readable chat only ever shows
   * the marker.
   */
  internalDetail?: string;
}

export interface ProjectRunnerStatus {
  projectName: string;
  mode: string;
  activeJobId: string | null;
  activeExecution: CliExecution | null;
  quotaFallbackModel?: string | null;
  quotaFallbackReason?: string | null;
  queuedJobIds: string[];
  /**
   * Human-readable reason recorded the last time the runner mode changed
   * (e.g. `auto-failure circuit-breaker: 3x same job 'foo' did not reach
   * review`, `api-toggle`, `supervisor pause: ...`). Lets the lane chip
   * distinguish operator-initiated `manual` / `paused` transitions from
   * system-initiated ones. Null on legacy in-memory records.
   */
  modeReason?: string | null;
  /** UTC timestamp when the mode last changed. Null when not recorded. */
  modeChangedAt?: string | null;
  /**
   * Coarse classification of where the current mode came from. One of
   * `user`, `circuit-breaker`, `supervisor`, `system`. Computed on the
   * backend from `modeReason` so the frontend does not have to re-implement
   * the heuristic on every render.
   */
  modeSource?: 'user' | 'circuit-breaker' | 'supervisor' | 'system' | string | null;
  /** Current global auto-failure breaker state; `cooldown` means auto-resume is scheduled. */
  breakerState?: 'cooldown' | string | null;
  /** UTC instant when the global breaker cooldown expires. */
  breakerCooldownUntil?: string | null;
  /** Human-readable reason for the active global breaker cooldown. */
  breakerReason?: string | null;
  /** Account-level CLI capabilities temporarily held until their reset/probe time. */
  providerLimits?: ProviderLimitStatus[];
  /** Number of global breaker trips since backend startup. */
  breakerTripCount?: number;
  /**
   * Backend role assigned via `Runner:Role` config (ADR-0044). `orchestrator`
   * runs the auto-pickup loop (stable seat); `test-subject` structurally
   * disables auto-pickup so the dev backend can be observed by Playwright
   * specs without racing stable on the shared workspace. Defaults to
   * `orchestrator` when older backends return without the field.
   */
  role?: 'orchestrator' | 'test-subject' | string | null;
  /**
   * Mode the operator asked for while tasks were still running. Non-null only
   * when a `PUT /api/runner/{project}/mode` with `manual` / `paused` arrived
   * while tasks were active. Auto admission closes immediately, and the runner
   * applies the value after the request-time active set drains. See ADR-0044.
   */
  pendingMode?: string | null;
  /** Job id of the sole remaining request-time task; null while several remain. */
  pendingModeWillApplyAfter?: string | null;
  /** Remaining tasks from the active snapshot captured when the change was requested. */
  pendingModeActiveTaskCount?: number;
  /** Title of the sole remaining snapshot task, when exactly one remains. */
  pendingModeActiveTaskTitle?: string | null;
}

export interface ProviderLimitStatus {
  cliType: string;
  observedAt: string;
  limitedUntil: string;
  reason: string;
  resetTimeReported: boolean;
}

/**
 * Response body for `PUT /api/runner/{project}/mode` (ADR-0044).
 * `applied: true` means the live mode moved immediately; `applied: false`
 * means the change is queued behind the request-time active task set, in which case
 * {@link pendingMode} + {@link willApplyAfterJobId} carry the deferred
 * value. `willApplyAfterJobId` is populated only when one snapshot task remains.
 */
export interface SetRunnerModeResponse {
  applied: boolean;
  mode: string;
  pendingMode?: string | null;
  willApplyAfterJobId?: string | null;
}

export interface RunnerStatus {
  projects: Record<string, ProjectRunnerStatus>;
  /** Active bounded local npm-shim repair failures; healthy CLIs are omitted. */
  cliRepairs?: LocalCliRepairStatus[];
}

export interface LocalCliRepairStatus {
  cliType: string;
  outcome: 'repaired' | 'failed' | string;
  occurredAt: string;
  versionBefore?: string | null;
  versionAfter?: string | null;
  detail: string;
}

/**
 * Cycle 5 single-round-trip snapshot for the project-detail panel.
 * Returned by GET /api/projects/{projectName}/snapshot. Matches the
 * anonymous-object shape that ProjectSnapshotEndpoints emits; the
 * standalone endpoints (settings, runner-status, orchestrator-log,
 * orchestrator-session, review-decisions-pending, runner-pending-decisions)
 * remain available so other consumers don't churn.
 */
export interface ProjectSnapshot {
  project: string;
  capturedAt: string;
  paths: {
    path: string;
    rootPath: string | null;
    repositoryPath: string | null;
  };
  settings: {
    autoCommit: boolean;
    crashRecoveryEnabled: boolean;
    autoPushStrategy: 'never' | 'on-completed' | 'always-immediate';
    runnerMode: string | null;
    orchestratorModel: string | null;
    /** F35: every lane resolved to its effective sort strategy (defaults filled in). */
    laneSortStrategies?: Record<string, string>;
  };
  runnerStatus: ProjectRunnerStatus | null;
  orchestratorLogTail: OrchestratorLogEntry[];
  orchestratorSession: OrchestratorSession | null;
  reviewDecisionsPending: { jobId: string; title: string; reason: string | null }[];
  runnerPendingDecisions: { jobId: string; title: string; kind: string; reason: string | null; detectedAt: string }[];
  /** PUB-1: derived publish targets + pending deltas for the Hub publish badges. */
  publishTargets: PublishTarget[];
  queueHealth: ProjectQueueHealth;
}

/**
 * PUB-1 - a derived publish target for a project, rendered as a Hub badge like
 * "NuGet 0.3.1 -> 4 tasks pending". Repo-fact-derived and read-only. A package
 * that has never been released carries `firstPublishPending` (no version, no
 * count); `pendingCount === 0` is a quiet state (no badge). `pendingCount` is
 * null when no baseline could be derived from git (see `referenceKind === 'none'`).
 */
export interface PublishTarget {
  /** Stable id: 'package:npm', 'package:nuget', or 'website'. */
  id: string;
  /** Wire value is the camelCase enum name (JsonStringEnumConverter). */
  kind: 'package' | 'website';
  /** 'npm' | 'nuget' for packages; null for websites. */
  ecosystem: string | null;
  /** Short label the badge renders: 'npm', 'NuGet', 'Website'. */
  label: string;
  /** Package id/name (e.g. 'coding-agent-chat'); null for websites / unknown. */
  packageName: string | null;
  /** Current published version (e.g. '0.3.1'); null when never released. */
  currentVersion: string | null;
  /** A package with a release workflow but no tag: never published. */
  firstPublishPending: boolean;
  /** Merged commits since the reference touching this target's scope; null = no baseline. */
  pendingCount: number | null;
  /** How the baseline was set: 'tag' | 'release-tag' | 'pages-branch' | 'none'. */
  referenceKind: string;
  /** The reference the baseline resolves to (tag name or date); null for 'none'. */
  reference: string | null;
}

export type PublishAutomationMode = 'manual' | 'suggest' | 'auto';

export interface PublishPendingTask {
  taskId: string;
  taskKey: string;
  title: string;
  taskType: 'bug' | 'feature' | 'chore';
}

export interface PublishWorkflowRun {
  project: string;
  targetId: string;
  workflow: string;
  runId: number | null;
  status: string;
  conclusion: string | null;
  version: string | null;
  url: string | null;
  triggeredAt: string;
  error: string | null;
}

export interface PublishActionPanel {
  project: string;
  target: PublishTarget;
  automationMode: PublishAutomationMode;
  pendingTasks: PublishPendingTask[];
  suggestedVersion: string | null;
  notice: string | null;
  lastRun: PublishWorkflowRun | null;
}

/**
 * PUB-1 - per-task publish chip signal folded onto an accepted task
 * (`TaskInfo.publishSignal`): which publish targets the task's merged work is
 * publishable to. Renders "publishable: npm, website" on the card / detail.
 */
export interface TaskPublishSignal {
  /** Target ids the task is publishable to ('package:npm', 'website', ...). */
  targetIds: string[];
  /** Short labels for the chip, in target order (e.g. 'npm', 'Website'). */
  labels: string[];
}

export type TestRunMatchQuality = 'none' | 'perfect' | 'contains-diff' | 'does-not-contain-diff';
export type TestEvidenceState = 'unassigned' | 'pending' | 'proven' | 'failed' | 'not-proven' | 'not-applicable';

export interface TaskTestEvidenceSource {
  kind: 'project-test-run' | 'review-build-tests' | 'review-aspects' | 'build-test-gate' | 'pre-develop-build-gate' | 'pre-main-test-gate' | string;
  id: string;
  commit: string;
  result: 'passed' | 'blocked' | 'failed' | 'not-proven' | 'not-applicable' | string;
  observedAt: string | null;
  summary: string;
  reason: string;
  reportRef: string;
}

export interface TaskTestRunEvidence {
  runId: string | null;
  runCommit: string | null;
  runState: 'planned' | 'running' | 'completed' | null;
  runResult: 'passed' | 'failed' | 'canceled' | null;
  matchQuality: TestRunMatchQuality;
  direction: 'none' | 'exact' | 'after' | 'before';
  distance: number | null;
  diffContained: boolean;
  evidenceState: TestEvidenceState;
  awaitingEvidence: boolean;
  summary: string;
  /** SHA-linked task-owned grades and gate logs that complement project TestRunStore runs. */
  sources?: TaskTestEvidenceSource[];
}

export interface CrashRecoveryPending {
  id: string;
  createdAt: string;
  projectName: string;
  jobId: string | null;
  repoRoot: string;
  files: string[];
  message: string;
  reason: string;
  classification: 'trivial' | 'review-required';
}

export interface CrashRecoveryActionResult {
  status: 'committed' | 'dismissed' | 'failed' | 'not-found' | 'nothing-to-commit' | string;
  pending: CrashRecoveryPending | null;
  commitSha: string | null;
  error: string | null;
}

export interface ProjectQueueHealthLocation {
  id: string;
  lane: string;
  hasJobJson: boolean;
  path: string;
}

export interface ProjectQueueHealthDuplicate {
  id: string;
  locations: ProjectQueueHealthLocation[];
}

export interface ProjectQueueHealthStateMismatch {
  id: string;
  lane: string;
  state: string | null;
  path: string;
}

export interface ProjectQueueHealth {
  severity: 'ok' | 'warning' | 'critical' | string;
  issueCount: number;
  missingJobJson: ProjectQueueHealthLocation[];
  duplicates: ProjectQueueHealthDuplicate[];
  stateMismatches: ProjectQueueHealthStateMismatch[];
}

export interface CliSettings {
  path: string;
  available: boolean;
  version: string | null;
  hasToken: boolean;
}

// ── End of shared kernel ──
//
// Cycle 9i: Feature-specific types (quota, cli catalog/usage,
// project-token-usage, etc.) are no longer re-exported from this file.
// Import them from the relevant feature barrel instead, e.g.:
//
//   import type { QuotaReport } from '../features/quota';
//   import type { CliUsageReport } from '../features/cli';
//   import type { ProjectTokenHeatmap } from '../features/project-token-usage';
//
// See frontend/AGENTS.md "Feature folders + barrel imports" for the
// rule and rationale.
