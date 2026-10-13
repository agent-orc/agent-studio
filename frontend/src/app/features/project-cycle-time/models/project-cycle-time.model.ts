/**
 * Wire model of `GET /api/projects/{project}/cycle-time?window=7d|30d|all`.
 * Mirrors `backend/Features/Projects/CycleTime/*` records (camelCase on the wire).
 */

export type CycleTimeWindow = '1d' | '7d' | '30d' | 'all';

export const CYCLE_TIME_WINDOWS: readonly CycleTimeWindow[] = ['1d', '7d', '30d', 'all'];

/** Additive lane stages in lane order; the sum equals the lead time of a task. */
export const CYCLE_TIME_STAGE_KEYS = [
  'preparation',
  'queueWait',
  'coding',
  'reviewWait',
  'testGate',
  'reviewOther',
  'integration',
  'humanReview',
  'completedBeforeReopen',
  'unattributed',
] as const;

export type CycleTimeStageKey = (typeof CYCLE_TIME_STAGE_KEYS)[number];

export type CycleTimeStageSeconds = Record<Exclude<CycleTimeStageKey, 'completedBeforeReopen'>, number>
  & { completedBeforeReopen?: number };

export interface FlowSegment {
  class: 'active' | 'resourceWait' | 'systemHold' | 'humanWait' | 'dependencyWait';
  step: string | null;
  resource: string | null;
  kind: string | null;
  start: string;
  end: string;
  seconds: number;
  attemptId: string | null;
  executor: string | null;
  tokens: number;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;
  costUsd: number;
  calls: number;
  unpricedCalls: number;
}

export interface FlowEpisode {
  start: string;
  end: string;
  machineDoneAt: string | null;
  segments: FlowSegment[];
  reviewAttempts: number;
  reviewAttemptsWithoutVerdict: number;
  dataGaps: string[];
  episodeSeconds: number;
  machineCycleSeconds: number;
  activeSeconds: number;
  resourceWaitSeconds: number;
  systemHoldSeconds: number;
  humanWaitSeconds: number;
  dependencyWaitSeconds: number;
  largestResource: string | null;
  tokens: number;
  costUsd: number;
  calls: number;
  unpricedCalls: number;
}

export interface TaskCycleTimeRow {
  taskId: string;
  taskKey: string;
  title: string;
  terminalState: string;
  watchPath: string;
  createdAt: string;
  firstClaimedAt: string | null;
  completedAt: string;
  completionSource: 'ledger' | 'lane-entry' | 'backfill' | string;
  completionKind?: 'first' | 'after-rework' | null;
  reAcceptanceArtefacts?: number;
  flow?: FlowEpisode | null;
  episodeSeconds?: number | null;
  machineCycleSeconds?: number | null;
  activeSeconds?: number | null;
  resourceWaitSeconds?: number | null;
  systemHoldSeconds?: number | null;
  humanWaitSeconds?: number | null;
  dependencyWaitSeconds?: number | null;
  machineDoneAt?: string | null;
  largestResource?: string | null;
  reviewAttempts?: number;
  reviewAttemptsWithoutVerdict?: number;
  segments?: FlowSegment[] | null;
  episodeTokens?: number;
  episodeCostUsd?: number;
  episodeCalls?: number;
  episodeUnpricedCalls?: number;
  cardTokens?: number;
  cardCostUsd?: number;
  /** Null for backfilled rows: the reconstructed completion dates the task but explains no stage durations. */
  stages: CycleTimeStageSeconds | null;
  reviewRunSeconds: number;
  leadTimeSeconds: number;
  cycleTimeSeconds: number | null;
  codingRuns: number;
  reviewRounds: number;
  bounceRounds: number;
  integrationAttempts: number;
  integrationOutcome: string | null;
  integrationStage: string | null;
  dataGaps: string[];
  /** Backward lane moves (any level drop, including runner lease recovery). */
  backwardTransitions: number;
  /** Present only with `detail=transitions` or on the per-task endpoint. */
  transitions?: TaskLaneTransition[] | null;
}

export type TransitionDirection = 'forward' | 'backward' | 'lateral';

export interface TaskLaneTransition {
  at: string;
  from: string;
  to: string;
  direction: TransitionDirection;
  /** Seconds spent in `from` before this move; null when the stay start is unknown. */
  dwellSeconds: number | null;
  actor: string;
  actorKind: 'runner' | 'review' | 'human' | 'orchestrator' | 'system' | 'external' | string;
  cause: string;
  causeDetail: string | null;
  attemptId: string | null;
  /** Backward moves only: seconds until the task got back to the level it fell from. */
  reworkSeconds: number | null;
}

export interface CycleTimeTransitionCell {
  from: string;
  to: string;
  count: number;
  direction: TransitionDirection;
}

export interface CycleTimeLaneDwell {
  lane: string;
  stays: number;
  p50Seconds: number | null;
  p90Seconds: number | null;
  maxSeconds: number | null;
  totalSeconds: number;
}

export interface CycleTimeBounceCause {
  cause: string;
  label: string;
  count: number;
  tasks: number;
  reworkKnown: number;
  reworkP50Seconds: number | null;
  reworkP90Seconds: number | null;
  reworkTotalSeconds: number;
  details: CycleTimeOutcomeCount[];
}

export interface CycleTimeLoopTask {
  taskId: string;
  taskKey: string;
  title: string;
  watchPath: string;
  backwardTransitions: number;
  leadTimeSeconds: number;
  causes: CycleTimeOutcomeCount[];
}

export interface CycleTimeTransitionSummary {
  totalTransitions: number;
  backwardTransitions: number;
  tasksWithBackwardTransitions: number;
  /** Lanes that occur as source or target, canonical order. */
  lanes: string[];
  cells: CycleTimeTransitionCell[];
  laneDwell: CycleTimeLaneDwell[];
  bounceCauses: CycleTimeBounceCause[];
  topLoops: CycleTimeLoopTask[];
}

export interface ProjectCycleTimeTaskResponse {
  project: string;
  capturedAt: string;
  task: TaskCycleTimeRow;
}

export type CycleTimeAggregateKind = 'stage' | 'rollup' | 'count' | 'flow';

export interface FlowDimensionAggregate {
  id: string;
  label: string;
  class: string;
  duration: CycleTimeAggregate;
  tokens: number;
  inputTokens?: number;
  outputTokens?: number;
  cacheReadTokens?: number;
  cacheCreationTokens?: number;
  costUsd: number;
  unpricedCalls: number;
  costPerCardP50Usd: number | null;
  shareOfMachineCycle: number | null;
  excluded: boolean;
}

export interface CycleTimeThroughputDay {
  day: string;
  delivered: number;
  machineDone: number;
  completed: number;
  reAcceptanceArtefacts: number;
}

export interface CycleTimeAggregate {
  stage: string;
  label: string;
  kind: CycleTimeAggregateKind;
  unit: 'seconds' | 'count';
  highlighted: boolean;
  count: number;
  p50: number | null;
  p90: number | null;
  max: number | null;
  mean: number | null;
  total: number;
}

export interface CycleTimeOutcomeCount {
  outcome: string;
  count: number;
}

export interface ProjectCycleTimeCoverage {
  tasksInProject: number;
  /** Terminal tasks (completed or archived) at any time, regardless of completion evidence. */
  tasksTerminal: number;
  tasksInWindow: number;
  excludedNoCompletionTimestamp: number;
  excludedInFlight: number;
  excludedEpics: number;
  tasksWithoutLedger: number;
  tasksWithLaneEntryCompletion: number;
  /** Window tasks whose completion was reconstructed by the backfill sidecar; they enter the lead-time rollup only. */
  tasksBackfilled: number;
  reAcceptanceArtefactsInWindow?: number;
}

export interface ProjectCycleTimeResponse {
  project: string;
  projectId: string | null;
  shortCode: string | null;
  window: string;
  capturedAt: string;
  since: string | null;
  coverage: ProjectCycleTimeCoverage;
  aggregates: CycleTimeAggregate[];
  integrationOutcomes: CycleTimeOutcomeCount[];
  transitions: CycleTimeTransitionSummary;
  tasks: TaskCycleTimeRow[];
  flow?: CycleTimeAggregate[];
  steps?: FlowDimensionAggregate[];
  resources?: FlowDimensionAggregate[];
  holds?: FlowDimensionAggregate[];
  human?: FlowDimensionAggregate[];
  throughputPerDay?: CycleTimeThroughputDay[];
}
