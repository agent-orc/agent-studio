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
  developState?: 'green' | 'running' | 'red' | null;
  lastGreenSha?: string | null;
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
  /** Pipeline delivery-target baseline (AGT-3309); absent from an older backend. */
  baseline?: PipelineBaseline | null;
}

/** Null values are unknown inputs, never zero. */
export interface PipelineBaseline {
  leadTimeMedianMinutes: number | null;
  leadTimeP90Minutes: number | null;
  leadTimeCards: number;
  leadTimeUnknownCards: number;
  gateMinutesPerCardMedian: number | null;
  gateMinutesPerCardP90: number | null;
  gateMinutesCards: number;
  gateMinutesUnknownCards: number;
  fullSuiteRuns: number | null;
  fullSuiteRunsPerDay: number | null;
  redDevelopMedianMinutes: number | null;
  redDevelopP90Minutes: number | null;
  gateSourceBytesMedian: number | null;
  gateSourceBytesP90: number | null;
  gateSourceSecondsMedian: number | null;
  gateSourceSecondsP90: number | null;
  gateSourceRuns: number;
}

/** Installation-wide board handler latency and git spawns per request. */
export interface BoardReadMetric {
  label: string;
  name: string;
  requests: number;
  p50Ms: number | null;
  p95Ms: number | null;
  gitSpawnsPerRequest: number | null;
}

export interface GateEconomicsResponse {
  window: string;
  asOfUtc: string;
  rows: GateEconomicsRow[];
  /** The integration-lane metrics always cover this many hours, whatever the window. */
  laneWindowHours?: number;
  boardRead?: BoardReadMetric[] | null;
  boardReadWindowMinutes?: number | null;
}
