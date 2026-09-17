// Mirror of the C# DTO in update-service/Models.cs. Keep in sync; the wire
// shape is the contract between the standalone UpdateService (port 5039)
// and the FE banner/dev-tools surface.

export interface UpdateStatus {
  phase: UpdatePhase;
  phaseLabel: string | null;
  message: string | null;
  currentRunId: string | null;
  startedAt: string | null;
  finishedAt: string | null;
  headLocal: string;
  headOrigin: string | null;
  behindBy: number;
  pendingCommits: CommitInfo[];
  lastFetchAt: string | null;
  lastUpdateAt: string | null;
  lastSuccessAt: string | null;
  // ADR-0031: when the most recent run finished. The FE shows the green
  // completion toast for `doneLingerSeconds` (default 60 s) after this
  // moment, even when the user joined the page while idle-polling.
  lastRunFinishedAt: string | null;
  lastRunHeadBefore: string | null;
  lastRunHeadAfter: string | null;
  isRunning: boolean;
  backendReachable: boolean;
  serviceVersion: string;
  productVersion: string;
  runningVersion: RuntimeVersion | null;
  mainVersion: BranchVersion | null;
  developVersion: BranchVersion | null;
  mode: 'manual' | 'scheduled';
  verificationFailures: VerificationFailure[] | null;
  autoRollbackEnabled: boolean;
  releaseComparison?: ReleaseComparison | null;
}

export interface ReleaseComparison {
  allowed: boolean;
  direction: 'SameVersion' | 'Upgrade' | 'Downgrade' | 'Divergence' | 'Unknown';
  summary: string;
  errors: string[];
  latestApprovedTag: string | null;
  offline: boolean;
}

export interface RuntimeVersion {
  version: string;
  commit: string;
  deployedAt: string | null;
  tag?: string | null;
  dirty?: boolean;
  builtAt?: string | null;
  integrity?: string | null;
  codingAgentRunner?: ReleaseArtifactIdentity | null;
  codingAgentChat?: ReleaseArtifactIdentity | null;
  legacy?: boolean;
}

export interface ReleaseArtifactIdentity {
  name: string;
  version: string;
  tag: string;
  commit: string;
  integrity: string;
}

export interface BranchVersion {
  branch: 'main' | 'develop' | string;
  commit: string;
  commitAt: string | null;
  aheadBy: number;
  behindBy: number;
}

export interface CommitInfo {
  sha: string;
  subject: string;
  author: string;
  authorDate: string;
}

// ADR-0031: phase vocabulary widened from the original 4-step shell into
// the 9-phase pipeline. FE renders by `phaseLabel` first, falls back to
// `phase`. New phases are additive; tolerate unknown strings.
export type UpdatePhase =
  | 'idle'
  | 'preparing'
  | 'pausing-runners'
  | 'pulling'
  | 'building'
  | 'restarting'
  | 'verifying-after-restart'
  | 'resuming'
  | 'rolling-back'
  | 'done'
  // AGT-2862: terminal. The backend restarted and verified at the intended
  // identity, the frontend dev server never answered, and the checkout was
  // deliberately left on the candidate so the running backend keeps its
  // source. `isRunning` is false, so the block modal comes down and the
  // operator decides.
  | 'degraded'
  | 'failed';

export interface VerificationFailure {
  step: string;
  observed: string | null;
  expected: string | null;
}

export interface UpdateHistoryEntry {
  runId: string;
  startedAt: string;
  finishedAt: string | null;
  status: 'ok' | 'degraded' | 'failed' | 'aborted';
  headBefore: string;
  headAfter: string;
  durationSeconds: number;
  error: string | null;
  trigger: 'manual' | 'scheduled' | 'api';
  verificationFailures?: VerificationFailure[] | null;
  rollbackStatus?: 'ok' | 'failed' | null;
  runFolder?: string | null;
  intendedTag?: string | null;
  observedTag?: string | null;
  releaseDirection?: string | null;
  manifestIntegrity?: string | null;
}

export interface TriggerRequest {
  reason?: string;
  force?: boolean;
}

export interface TriggerResponse {
  runId: string;
  phase: UpdatePhase;
  message: string;
}

export interface RollbackRequest {
  runId: string;
}

export interface RollbackResponse {
  runId: string;
  phase: UpdatePhase;
  message: string;
}
