/**
 * AGT-2721 global Watcher models. Mirror the backend records in
 * `backend/Features/Watcher/`. See the decision dossier under
 * `docs/operations/orchestrator-waechter/` for what each field means.
 */

/** Bus participant the Watcher publishes under. Identifies its rows in Activity. */
export const WATCHER_PARTICIPANT_ID = 'orchestrator:global-watcher';

/** Bus topics the Watcher produces; the feed keys its decision surface off these. */
export const WATCHER_TOPICS = {
  findingRaised: 'watcher-finding-raised',
  analysisComplete: 'watcher-analysis-complete',
  decisionRequired: 'watcher-decision-required',
  decisionRecorded: 'watcher-decision-recorded',
  caseResolved: 'watcher-case-resolved',
  contingentExhausted: 'watcher-contingent-exhausted',
} as const;

export type WatcherDetectorClass =
  | 'repetition'
  | 'contradiction'
  | 'silence'
  | 'drift'
  | 'hygiene';

export type WatcherDecisionState = 'pending' | 'approved' | 'edited' | 'merged' | 'rejected';

export interface WatcherEvidenceItem {
  label: string;
  value: string;
  source: string;
  /** False when the fact is absent. Render it as missing, never as empty. */
  available: boolean;
}

export interface WatcherCase {
  id: string;
  fingerprint: string;
  fingerprintDigest: string;
  detectorClass: WatcherDetectorClass;
  detectorRule: string;
  title: string;
  project: string | null;
  firstSeenAtUtc: string;
  lastSeenAtUtc: string;
  occurrences: number;
  sweepCount: number;
  affectedCards: string[];
  evidence: WatcherEvidenceItem[];
  evidenceDigest: string;
  uncertainCause: boolean;
  state: string;
  proposalId: string | null;
  terminalReason: string | null;
  updatedAtUtc: string;
}

export interface WatcherModelRecommendation {
  tier: string;
  model: string;
  thinkingLevel: string | null;
  policyVersion: string;
  score: number;
  correctnessFloorTier: string | null;
  reason: string;
}

export interface WatcherModelCall {
  purpose: string;
  model: string;
  thinkingLevel: string | null;
  inputTokens: number;
  outputTokens: number;
  /** Null when the price catalogue has no entry. Never render it as zero. */
  costUsd: number | null;
  priceKnown: boolean;
  atUtc: string;
}

export interface WatcherProposal {
  id: string;
  caseId: string;
  fingerprint: string;
  fingerprintDigest: string;
  detectorClass: WatcherDetectorClass;
  detectorRule: string;
  project: string | null;
  /** 'new-card' or 'comment' on the card that already carries the fingerprint. */
  kind: string;
  draft: {
    title: string;
    promptMarkdown: string;
    taskType: string;
    tags: string[];
    relatedTo: string[];
  };
  recommendation: WatcherModelRecommendation;
  evidenceDigest: string;
  evidence: WatcherEvidenceItem[];
  modelCalls: WatcherModelCall[];
  createdTaskKey: string | null;
  commentedOnTaskKey: string | null;
  decision: {
    state: WatcherDecisionState;
    decidedAtUtc: string | null;
    decidedBy: string | null;
    reason: string | null;
    mergedIntoTaskKey: string | null;
  };
  createdAtUtc: string;
  updatedAtUtc: string;
}

export interface WatcherContingentLimits {
  modelCallsPerDay: number | null;
  modelCallsPerWeek: number | null;
  tokensPerDay: number | null;
  tokensPerWeek: number | null;
  proposalsPerDay: number | null;
  proposalsPerWeek: number | null;
  commentsPerDay: number | null;
  commentsPerWeek: number | null;
}

export interface WatcherContingentUsage {
  modelCallsDay: number;
  modelCallsWeek: number;
  tokensDay: number;
  tokensWeek: number;
  proposalsDay: number;
  proposalsWeek: number;
  commentsDay: number;
  commentsWeek: number;
  costUsdDay: number;
  costUsdWeek: number;
  unpricedCallsDay: number;
}

export interface WatcherContingentSnapshot {
  limits: WatcherContingentLimits;
  usage: WatcherContingentUsage;
  dayStartUtc: string;
  weekStartUtc: string;
  exhaustedDimensions: string[];
  /** Cases counted but not analysed because the budget ran out. */
  backlogCases: number;
  proposalsBlocked: boolean;
  modelCallsBlocked: boolean;
  costUsdDayDisplay: string;
}

export interface WatcherSnapshot {
  enabled: boolean;
  lastRunAtUtc: string | null;
  lastRunFailedAtUtc: string | null;
  sweeps: number;
  signalsCollected: number;
  findingsDetected: number;
  openCases: number;
  pendingProposals: number;
  backlogCases: number;
  proposalsCreatedLastRun: number;
  commentsAppendedLastRun: number;
  modelCallsLastRun: number;
  resolvedLastRun: number;
  lastRunElapsedMs: number;
  lastError: string | null;
}

export interface WatcherStatus {
  snapshot: WatcherSnapshot;
  enabled: boolean;
  intervalSeconds: number;
  analysisEnabled: boolean;
  analysisTier: string;
  detectorClasses: WatcherDetectorClass[];
  sources: string[];
  contingent: WatcherContingentSnapshot;
  stale: boolean;
  storeAvailable: boolean;
}

export interface WatcherDecisionRequest {
  decision: Exclude<WatcherDecisionState, 'pending'>;
  /** Required on a rejection; it feeds the visible suppression list. */
  reason?: string | null;
  /** Required on a merge. */
  mergeIntoTaskKey?: string | null;
}

export interface WatcherSuppressionView {
  fingerprint: string;
  detectorClass: WatcherDetectorClass;
  reason: string;
  createdAtUtc: string;
  expiresAtUtc: string;
  createdBy: string | null;
  active: boolean;
}
