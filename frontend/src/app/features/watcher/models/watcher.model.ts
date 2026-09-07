/**
 * Global Orchestrator Watcher (orchestrator-waechter dossier §10) review-mode
 * models. Mirrors the backend `AgentStudio.Watcher` contracts.
 */

export type WatcherDetectorClass = 'repetition' | 'contradiction' | 'silence' | 'drift' | 'hygiene';

export type WatcherCaseState = 'open' | 'decision-required' | 'resolved' | 'gave-up' | 'suppressed';

export interface WatcherCase {
  id: string;
  fingerprint: string;
  detectorClass: WatcherDetectorClass | string;
  project: string;
  affectedCards: string[];
  firstSeenUtc: string;
  lastSeenUtc: string;
  occurrenceCount: number;
  sweepCount: number;
  state: WatcherCaseState | string;
  lastSummary: string;
  proposalId?: string | null;
  proposalJobId?: string | null;
  isCommentOnly: boolean;
  gaveUpReason?: string | null;
}

export type WatcherProposalOutcome = 'approved' | 'edited' | 'merged' | 'rejected';

export interface WatcherProposalDecision {
  outcome: WatcherProposalOutcome | string;
  reason?: string | null;
  mergedIntoJobId?: string | null;
  decidedAtUtc: string;
  decidedBy: string;
}

export interface WatcherProposal {
  id: string;
  caseId: string;
  detectorClass: WatcherDetectorClass | string;
  fingerprint: string;
  project: string;
  jobId?: string | null;
  isComment: boolean;
  commentedJobId?: string | null;
  title: string;
  recommendedModel: string;
  recommendedThinkingLevel: string;
  tags: string[];
  createdAtUtc: string;
  decision?: WatcherProposalDecision | null;
}

export interface WatcherProposalDecisionRequest {
  outcome: WatcherProposalOutcome;
  reason?: string | null;
  mergedIntoJobId?: string | null;
}

export interface WatcherContingentUsage {
  periodKey: string;
  tokensUsed: number;
  modelCalls: number;
  proposalsCreated: number;
  commentsAppended: number;
}

export interface WatcherContingentBudgets {
  dailyTokenBudget: number;
  weeklyTokenBudget: number;
  dailyProposalBudget: number;
  weeklyProposalBudget: number;
  dailyModelCallBudget: number;
  weeklyModelCallBudget: number;
}

export interface WatcherContingentSnapshot {
  budgets: WatcherContingentBudgets;
  day: WatcherContingentUsage;
  week: WatcherContingentUsage;
  modelCallsExhausted: boolean;
  proposalsExhausted: boolean;
}

export interface WatcherRunSnapshot {
  lastRunAtUtc: string | null;
  sweepId: string;
  enabled: boolean;
  observationsCollected: number;
  casesUpdated: number;
  proposalsCreated: number;
  commentsAppended: number;
  contingentBlocked: number;
  analysisCalls: number;
  error?: string | null;
}

export interface WatcherStatusResponse {
  options: { enabled: boolean; intervalSeconds: number; persistenceSweepsBeforeProposal: number; suppressionDays: number };
  lastRun: WatcherRunSnapshot;
  contingent: WatcherContingentSnapshot | null;
}
