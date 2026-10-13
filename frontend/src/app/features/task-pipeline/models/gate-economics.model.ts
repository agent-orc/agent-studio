export interface GateEconomicsRow {
  project: string;
  gatedMerges: number;
  gateGreen: number;
  gateRed: number;
  gateCancelled: number;
  gateMinutesMedian: number | null;
  gateMinutesP90: number | null;
  queueWaitP90Ms: number | null;
  exactReuseRate: number | null;
  disjointReuseRate: number | null;
  fullSuiteShare: number | null;
  sameShaRetestRate: number | null;
  lastNightlyGuard: string | null;
  deliveryLagP50Minutes: number | null;
  deliveryLagP95Minutes: number | null;
  oldestWaitingMinutes: number | null;
  preparationP50Minutes: number | null;
  queued: number;
  prepared: number;
  gating: number;
  readyToPublish: number;
  laneGateDurationP50Minutes: number | null;
  laneGateDurationP95Minutes: number | null;
  laneGateGreen: number;
  laneGateRed: number;
  laneGateCancelled: number;
  gateTokenWaitP50Ms: number | null;
  gateTokenWaitP95Ms: number | null;
  gateHoldingProject: string | null;
  mergeLockWaitP95Ms: number | null;
  publicationLockWaitP95Ms: number | null;
  ejectedConflict: number;
  ejectedAgentRound: number;
  ejectedRed: number;
  ejectedError: number;
  arrivalsPerHour: number;
  mergesPerHour: number;
  projectShortCode: string | null;
  projectColor: string | null;
}

export interface GateEconomicsResponse {
  window: string;
  asOfUtc: string;
  rows: GateEconomicsRow[];
  /** The integration-lane metrics always cover this many hours, whatever the window. */
  laneWindowHours?: number;
}
