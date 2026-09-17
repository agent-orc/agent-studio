/** Git feature public API. Cycle 9h / ADR-0034. */
export type {
  GitFileChange,
  GitStatus,
  GitProjectSummary,
  GitHygieneStatus,
  TaskHygieneContext,
  TaskCommitInfo,
  TaskCommitDetail,
  LandedState,
  TaskProvenanceTransition,
  TaskProvenanceMerge,
  TaskLandedLadder,
  TaskCommitMembership,
  TaskProvenanceView,
  TaskProvenanceRecord,
  TaskMergeSignal,
  TaskIntegrationStatus,
  IntegrationStatusValue,
  TaskRepositoryIntegrationStatus,
  // Project Hub Git View inventory.
  GitBranchCategory,
  GitWorktreeEntry,
  GitBranchEntry,
  GitCommitEntry,
  GitTaskBadge,
  GitCommitRef,
  GitCommitPresence,
  GitDeploymentMarker,
  GitGraphCommit,
  GitHistoryPage,
  GitActiveCheckout,
  GitProjectInventory,
  IntegrationQueueState,
  IntegrationQueueItem,
  PublisherMergeItem,
  PromotionTaskItem,
  PromotionDiffView,
  ProjectIntegrationView,
  // Git-Management cleanup (AGT-2009).
  CleanupTargetKind,
  CleanupMergeStatus,
  CleanupCandidate,
  GitCleanupPlan,
  CleanupExecutionItem,
  CleanupActionOutcome,
  GitCleanupResult,
  // Stale-branch sweep (AGT-2794).
  BranchSweepMode,
  BranchSweepClass,
  BranchSweepCandidate,
  BranchSweepClassTotals,
  BranchSweepAgeBucket,
  BranchSweepDeletion,
  BranchRetentionWindows,
  BranchSweepReport,
  BranchSweepExecutionItem,
  BranchSweepExecutionResult,
  BranchSweepSettings,
} from './models/git.model';
export { isMergedIntegrationStatus } from './models/git.model';

// Project Hub Git View tree model (pure builder + node types).
export {
  buildGitTree,
  branchCategoryLabel,
} from './models/git-tree.model';
export { buildGitGraphRows } from './models/git-graph-layout.model';
export {
  buildGitCommitChips,
  type GitCommitChip,
  type GitCommitChipKind,
  type GitCommitChipTone,
} from './models/git-commit-chip.model';
export type {
  GitTreeGroupId,
  GitTreeGroup,
  GitTreeLeaf,
  GitTreeBranchNode,
  GitTreeWorktreeNode,
  GitTreeActiveNode,
} from './models/git-tree.model';
export type { GitGraphRow, GitGraphSegment } from './models/git-graph-layout.model';

export type { CommitSupersessionState } from './models/commit-supersession.model';
export {
  PENDING_ATTEMPT,
  commitSupersessionState,
  isReplacedCommit,
  isEffectiveDelivery,
} from './models/commit-supersession.model';
