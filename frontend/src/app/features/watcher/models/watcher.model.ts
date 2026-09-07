/**
 * Global Watcher view models (AGT-2721). Mirror the backend contracts in
 * `backend/Features/Watcher/`; see the decision dossier
 * `docs/operations/orchestrator-waechter/index.html` §10 for the concept.
 */

/** The five detector classes of dossier §10.2. */
export type WatcherDetectorClass =
  | 'repetition'
  | 'contradiction'
  | 'silence'
  | 'drift'
  | 'hygiene';

/** Consumption inside one rolling window. */
export interface WatcherContingentUsage {
  tokens: number;
  modelCalls: number;
  proposals: number;
  comments: number;
}

/** Per-day and per-week caps in tokens and in counts. */
export interface WatcherContingentBudget {
  dailyTokens: number;
  weeklyTokens: number;
  dailyModelCalls: number;
  weeklyModelCalls: number;
  dailyProposals: number;
  weeklyProposals: number;
  dailyComments: number;
  weeklyComments: number;
}

/**
 * The contingent as the operator sees it. `weeklyDollars` is `null` when any
 * call in the window had no catalog price - unknown price is rendered as
 * unknown, never as zero.
 */
export interface WatcherContingentSnapshot {
  budget: WatcherContingentBudget;
  daily: WatcherContingentUsage;
  weekly: WatcherContingentUsage;
  weeklyDollars: number | null;
  exhausted: boolean;
  /** Cases that are persistent but have no proposal because the budget ran out. */
  backlogCases: number;
  dailyTokensRemaining: number;
  weeklyTokensRemaining: number;
  dailyProposalsRemaining: number;
  weeklyProposalsRemaining: number;
}

export interface WatcherStatus {
  enabled: boolean;
  lastRunAtUtc: string | null;
  lastHeartbeatAtUtc: string | null;
  openCases: number;
  decisionsRequired: number;
  backlog: number;
  activeSuppressions: number;
  contingent: WatcherContingentSnapshot | null;
}
