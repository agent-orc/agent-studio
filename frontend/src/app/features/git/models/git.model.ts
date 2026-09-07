/**
 * Cycle 9 git feature models. Lifted out of the kitchen-sink
 * `models/job.model.ts` per ADR-0034 + the architecture review. The
 * legacy file re-exports these so existing imports keep working;
 * new code should import from `features/git/models/git.model`
 * directly so the feature boundary stays visible.
 */

export interface GitFileChange {
  status: string;
  path: string;
  added: number;
  removed: number;
}

export interface GitStatus {
  isRepo: boolean;
  branch: string | null;
  filesChanged: number;
  totalAdded: number;
  totalRemoved: number;
  files: GitFileChange[];
  error: string | null;
  /**
   * True when the live status was read from the task's own `task/<id>`
   * worktree (ASS-1731) rather than the project's main checkout. Drives the
   * header location label so the user can see *where* the shown tree lives:
   * `task/<id> (Worktree)` vs `<branch> (Haupt-Checkout)`. Defaults false for
   * sequential runs and after the worktree is torn down.
   */
  isWorktree: boolean;
}

export interface GitProjectSummary {
  projectName: string;
  rootPath: string;
  isRepo: boolean;
  branch: string | null;
  filesChanged: number;
  totalAdded: number;
  totalRemoved: number;
}

/** Coarse branch classification used by the Project Hub Git View tree. */
export type GitBranchCategory = 'main' | 'develop' | 'feature' | 'task' | 'runner' | 'other';

/** Compact task-card link attached to a branch, checkout, or commit. */
export interface GitTaskBadge {
  taskKey: string;
  key: string;
  title: string;
  lane: string;
}

/**
 * One checkout of the project repository (primary or an ADR-0052 per-task
 * worktree) as reported by the backend inventory. Mirrors backend
 * `GitWorktreeEntry`. The concrete on-disk {@link path} is always present so
 * the Git View can show where each checkout lives.
 */
export interface GitWorktreeEntry {
  path: string;
  branch: string | null;
  headSha: string | null;
  headShortSha: string | null;
  isPrimary: boolean;
  isDetached: boolean;
  isBare: boolean;
  task?: GitTaskBadge | null;
}

/** One local, remote, or folded local-and-origin branch in the Git View inventory. */
export interface GitBranchEntry {
  name: string;
  category: GitBranchCategory;
  tipSha: string | null;
  tipShortSha: string | null;
  isCurrent: boolean;
  upstream: string | null;
  ahead: number;
  behind: number;
  lastCommitSubject: string | null;
  lastCommitAtUtc: string | null;
  worktreePath: string | null;
  isLocal?: boolean;
  hasRemote?: boolean;
  remoteTipSha?: string | null;
  tasks?: GitTaskBadge[];
}

/** One commit in the Git View recent-history list. Mirrors backend `GitCommitInfo`. */
export interface GitCommitEntry {
  sha: string;
  shortSha: string;
  authorDateUtc: string;
  author: string;
  subject: string;
  filesChanged: number;
  added: number;
  removed: number;
}

export interface GitCommitRef {
  name: string;
  kind: 'head' | 'branch' | 'tag' | 'ref' | string;
  isRemote: boolean;
}

export interface GitCommitPresence {
  inIntegration: boolean;
  inRelease: boolean;
  integrationBranch: string;
  releaseBranch: string;
}

export interface GitDeploymentMarker {
  target: 'backend' | 'runner' | 'frontend' | string;
  sha: string;
  shortSha: string;
}

export interface GitGraphCommit extends GitCommitEntry {
  parentShas: string[];
  refs: GitCommitRef[];
  tasks: GitTaskBadge[];
  presence: GitCommitPresence | null;
  deployments: GitDeploymentMarker[];
}

export interface GitHistoryPage {
  offset: number;
  pageSize: number;
  nextOffset: number | null;
  hasMore: boolean;
  commits: GitGraphCommit[];
}

export interface GitActiveCheckout {
  task: GitTaskBadge;
  branch: string | null;
  headSha: string | null;
  location: 'local' | 'remote' | string;
  runner: string;
  worktreePath: string | null;
  activeSince: string | null;
}

/**
 * Read-only branch / worktree / first graph-page inventory for one project.
 * Mirrors backend `GitProjectInventory`; fetched from
 * `GET /api/git/inventory?project=<name>` and consumed by the Project Hub Git
 * View. `isRepo === false` with a populated {@link error} is the empty/error
 * signal (unknown project, no configured repository, or a non-git folder).
 */
export interface GitProjectInventory {
  projectName: string;
  repositoryPath: string | null;
  isRepo: boolean;
  currentBranch: string | null;
  worktrees: GitWorktreeEntry[];
  branches: GitBranchEntry[];
  recentCommits: GitCommitEntry[];
  history?: GitHistoryPage | null;
  activeCheckouts?: GitActiveCheckout[];
  deployments?: GitDeploymentMarker[];
  error: string | null;
}

export type IntegrationQueueState =
  | 'merged'
  | 'waiting'
  | 'conflict'
  | 'skipped'
  | 'legacy-unverifiable'
  | 'superseded';

export interface IntegrationQueueItem {
  taskId: string;
  taskKey: string;
  title: string;
  lane: string;
  stateSince: string;
  status: IntegrationQueueState;
  mergeSha: string | null;
  reason: string | null;
}

export interface PublisherMergeItem {
  taskKey: string;
  title: string | null;
  sha: string;
  shortSha: string;
  integratedAt: string;
  publisher: string;
  subject: string;
}

export interface PromotionTaskItem {
  taskKey: string;
  title: string | null;
  sha: string;
  shortSha: string;
  subject: string;
}

export interface PromotionDiffView {
  fromRef: string;
  toRef: string;
  fromSha: string | null;
  toSha: string | null;
  tasks: PromotionTaskItem[];
  files: GitFileChange[];
  filesChanged: number;
  added: number;
  removed: number;
}

/** Remote-ref-derived merge queue, publisher history, and release delta. */
export interface ProjectIntegrationView {
  project: string;
  isRepo: boolean;
  integrationRef: string;
  releaseRef: string;
  integrationHeadSha: string | null;
  releaseHeadSha: string | null;
  capturedAt: string;
  queue: IntegrationQueueItem[];
  publisherMerges: PublisherMergeItem[];
  promotion: PromotionDiffView;
  error: string | null;
}

/**
 * Git-Management cleanup (AGT-2009). Mirrors backend `GitCleanupService` models.
 * The plan is a read-only dry-run preview; execution acts on an operator-confirmed
 * subset and only ever removes GEMERGTES (AGT-1945 invariant).
 */
export type CleanupTargetKind = 'localBranch' | 'remoteBranch' | 'backupRef' | 'staleWorktree';
export type CleanupMergeStatus = 'merged' | 'unmerged' | 'notApplicable';

/** One row of the cleanup dry-run preview. Mirrors backend `CleanupCandidate`. */
export interface CleanupCandidate {
  kind: CleanupTargetKind;
  name: string;
  remote: string | null;
  tipSha: string | null;
  tipShortSha: string | null;
  mergeStatus: CleanupMergeStatus;
  /** True only when the item is provably safe to delete (merged / stale). */
  eligible: boolean;
  /** Merge evidence when eligible; why-kept reason otherwise. */
  reason: string;
}

/** The cleanup dry-run plan for one project. Mirrors backend `GitCleanupPlan`. */
export interface GitCleanupPlan {
  projectName: string;
  repositoryPath: string | null;
  isRepo: boolean;
  integrationBranch: string;
  candidates: CleanupCandidate[];
  error: string | null;
}

/** One confirmed item posted to the execute endpoint. Mirrors backend `CleanupExecutionItem`. */
export interface CleanupExecutionItem {
  kind: CleanupTargetKind;
  name: string;
  remote: string | null;
}

/** Per-item outcome of an executed cleanup. Mirrors backend `CleanupActionOutcome`. */
export interface CleanupActionOutcome {
  kind: CleanupTargetKind;
  name: string;
  remote: string | null;
  deleted: boolean;
  reason: string;
}

/** Result report of an executed cleanup. Mirrors backend `GitCleanupResult`. */
export interface GitCleanupResult {
  projectName: string;
  integrationBranch: string;
  isRepo: boolean;
  deletedCount: number;
  keptCount: number;
  actions: CleanupActionOutcome[];
  error: string | null;
}

/**
 * Repository hygiene snapshot. Mirrors backend `GitHygieneStatus`.
 *
 * Used by:
 *  - the project header dirty/unpushed badge (project-level fields only),
 *  - the job-detail review/completed hygiene strip (with the `job` overlay).
 *
 * Fetched from `GET /api/git/hygiene?project=<name>` (project) and
 * `GET /api/tasks/{id}/git/hygiene` (job). Both endpoints cache server-side
 * for ~3 s.
 */
export interface GitHygieneStatus {
  projectName: string;
  repoRoot: string | null;
  isRepo: boolean;
  branch: string | null;
  upstream: string | null;
  hasUpstream: boolean;
  ahead: number;
  behind: number;
  isDirty: boolean;
  stagedCount: number;
  unstagedCount: number;
  untrackedCount: number;
  lastCommitSha: string | null;
  lastCommitShortSha: string | null;
  lastCommitSubject: string | null;
  lastCommitAtUtc: string | null;
  job: TaskHygieneContext | null;
  error: string | null;
}

/**
 * Per-task hygiene overlay. Task-scoped fields only - repo-level
 * signals (ahead of upstream, push pending, untracked files in the
 * repo root) live on the surrounding {@link GitHygieneStatus} fields
 * and belong on the project-level surface, never on a per-task
 * detail page.
 */
export interface TaskHygieneContext {
  jobId: string;
  state: string;
  jobInfoCommitPresent: boolean;
  stampedCommitSha: string | null;
  acceptedTaskUncommitted: boolean;
}

export interface TaskCommitInfo {
  sha: string;
  shortSha: string;
  message: string;
  /** Registered repository id or remote URL that owns this commit. */
  repository?: string | null;
  filesChanged: number;
  files: string[];
  at: string;
  /**
   * How this commit was attributed to the task (ADR "Commit-Attribution-Regel").
   * One of `automatic` or `legacy` when the rule engine has not yet stamped it.
   */
  attribution?: string;
  /** Confidence of an automatic attribution (0..1); absent otherwise. */
  confidence?: number;
  /** Immutable delivery-generation identity stamped by remote attribution. */
  runAttemptId?: string | null;
  /** Runner that produced this delivery generation. */
  runnerId?: string | null;
  /** Result SHA fencing this delivery generation. */
  resultSha?: string | null;
  /** Result or task branch used to derive this commit. */
  branch?: string | null;
  /**
   * Attempt that replaced this historical commit. Superseded commits remain
   * selectable as history but are excluded from the current aggregate diff.
   */
  supersededByAttempt?: string | null;
  /** Exact replacement SHA produced by a conflict-free platform rebase. */
  supersededBySha?: string | null;
}

export interface TaskCommitDetail {
  commit: TaskCommitInfo | null;
  files: GitFileChange[];
}

/**
 * Commit-provenance & landed-state (ASS-1724). The derived view returned by
 * `GET /api/tasks/{id}/provenance`: persisted append-only facts (branch, base,
 * transitions, merge) plus everything recomputed live off the git graph
 * (landedState, ladder, per-commit membership). Never persisted; always read
 * fresh because develop/main move under it.
 */
export type LandedState = 'on-branch-only' | 'merged-to-develop' | 'released-to-main';

export interface TaskProvenanceTransition {
  lane: string;
  atUtc: string;
  branchTip: string | null;
  workBranchHead: string | null;
}

export interface TaskProvenanceMerge {
  mergeCommit: string | null;
  workBranchHeadBefore: string | null;
  workBranchHeadAfter: string | null;
  atUtc: string;
}

/**
 * The landed-ladder rungs: task/<id> -> develop @sha -> main @sha, each with
 * the live "HEAD now" SHA and whether the task's work has reached that rung.
 */
export interface TaskLandedLadder {
  branch: string;
  branchTip: string | null;
  integrationBranch: string;
  integrationHead: string | null;
  mergedToIntegration: boolean;
  releaseBranch: string;
  releaseHead: string | null;
  releasedToRelease: boolean;
}

/** One commit in the task's merge-set with its branch membership. */
export interface TaskCommitMembership {
  sha: string;
  shortSha: string;
  message: string;
  onTaskBranch: boolean;
  alsoOnIntegration: boolean;
  alsoOnRelease: boolean;
}

export interface TaskProvenanceView {
  branch: string;
  base: string | null;
  transitions: TaskProvenanceTransition[];
  merge: TaskProvenanceMerge | null;
  landedState: LandedState;
  ladder: TaskLandedLadder;
  commits: TaskCommitMembership[];
}

/**
 * The persisted provenance record, mirroring backend `TaskProvenance`. This is
 * the append-only fact block stored on `task.json` and surfaced on every board
 * card via `TaskInfo.provenance` - NOT the live-derived {@link TaskProvenanceView}
 * (which is only fetched per-task for the detail git pane).
 *
 * The card reads worktree lifecycle context from this record:
 *  - a real `task/<id>` worktree branch exists iff some transition carries a
 *    non-null `branchTip` (sequential runs in the shared checkout never cut one);
 *  - the newest transition's `branchTip` is the CURRENT attempt's tip, so a
 *    reissue points at the live worktree, not an earlier run;
 *  - `merge.mergeCommit` is historical attempt evidence written by the
 *    merge-into-develop step. It does not prove current target membership;
 *    accepted-card status comes from {@link TaskIntegrationStatus}.
 */
export interface TaskProvenanceRecord {
  branch: string;
  base: string | null;
  transitions: TaskProvenanceTransition[];
  merge: TaskProvenanceMerge | null;
}

/**
 * Compact, always-on board-card merge signal (AGT-2046). Mirrors backend
 * `TaskMergeSignal` and ships on every board card via `TaskInfo.mergeSignal`,
 * so the card renders a two-segment `[develop|main]` indicator without the
 * per-task graph query the detail header pays. Uses the same
 * worktree -> develop -> main semantics as {@link LandedState}: `inIntegration`
 * == the task's anchor is an ancestor of develop, `inRelease` == an ancestor of
 * main. Computed batched + cached per repository on the backend (never per
 * card). Null on cards with no committed/merged anchor yet.
 */
export interface TaskMergeSignal {
  /** The task's worktree branch name, for the card's branch chip + tooltip. */
  branch: string;
  /** True when the work is folded into the integration branch (develop). */
  inIntegration: boolean;
  /** True when the work has reached the release branch (main). */
  inRelease: boolean;
  /** Integration branch the signal was computed against (usually "develop"). */
  integrationBranch: string;
  /** Release branch the signal was computed against (usually "main"). */
  releaseBranch: string;
  /** Short attributed SHA proving develop membership; null when not in develop. */
  integrationSha: string | null;
  /** Short SHA of the anchor that reached main; null when not in main. */
  releaseSha: string | null;
}

/** One of the discrete integration verdicts (AGT-2202; `partial` added AGT-2171 fix). */
export type IntegrationStatusValue =
  | 'integrated'
  | 'partial'
  | 'pending'
  | 'conflict-skipped'
  | 'no-branch';

/**
 * AGT-2202 - the honest, git-derived integration verdict for an accepted card
 * (5-human-review / 6-completed / 7-archive): is this task's work actually folded
 * into the integration branch (develop)? Mirrors backend `TaskIntegrationStatus`
 * and ships via `TaskInfo.integration`. It is computed from attributed-commit
 * membership at the current target HEAD, batched and cached per repository.
 * `deliveryRef` comes from the same durable resolver truth used by acceptance,
 * so remote runner refs and evidenced local task refs render uniformly. Lane
 * state and remembered merge attempts are not membership inputs. Null on cards
 * not in an accepted lane.
 */
export interface TaskIntegrationStatus {
  /** integrated | pending | conflict-skipped | no-branch. */
  status: IntegrationStatusValue;
  /** Actual delivery ref from card truth; null only when no ref is evidenced. */
  deliveryRef: string | null;
  /** Short attributed SHA proving target-branch membership; null unless integrated. */
  sha: string | null;
  /** Integration branch the verdict was computed against (usually "develop"). */
  integrationBranch: string;
  /** Membership evidence or the reason it is not integrated. Tooltip + audit only. */
  detail: string | null;
  /** Typed current failure from the durable accepted-integration pipeline step. */
  failure?: TaskIntegrationFailure | null;
  /** Repository-scoped commit membership, including multi-repository deliveries. */
  repositories?: TaskRepositoryIntegrationStatus[];
}

export interface TaskRepositoryIntegrationStatus {
  repository: string;
  commits: TaskRepositoryCommitMembership[];
  integrationBranch: string;
  releaseBranch: string;
  onIntegrationBranch: boolean;
  onReleaseBranch: boolean;
  detail: string;
}

export interface TaskRepositoryCommitMembership {
  sha: string;
  onIntegrationBranch: boolean;
  onReleaseBranch: boolean;
}

export interface TaskIntegrationFailure {
  /** Stable machine-readable failure class. */
  code: string;
  /** Concise card label. */
  label: string;
  /** Operator-facing explanation without raw exception wording. */
  reason: string;
  /** Whether the focused rebase recovery action can resolve this class. */
  rebaseRecoveryAvailable: boolean;
}
