/**
 * Wire-level fixtures for the flow-health page: the responses of the joined
 * read models for four situations. Unit specs feed them through
 * `joinFlowSources`; the Playwright spec serves them from mocked routes.
 * Type-only imports keep this file loadable outside Angular.
 */
import type { GateEconomicsResponse, GateEconomicsRow } from '../../task-pipeline/models/gate-economics.model';
import type { PipelineHealthSnapshot } from '../../task-pipeline/models/pipeline-health.model';
import type { ProjectCycleTimeResponse, TaskCycleTimeRow } from '../../project-cycle-time/models/project-cycle-time.model';
import type { BatchGateReport } from '../../../models/batch-gate.model';
import type { FlowHealthResponses, FlowWindow } from '../models/flow-health.model';

export type FlowFixtureKind = 'noData' | 'healthyWeek' | 'reviewJam' | 'gateJam';
export const FLOW_FIXTURE_KINDS: readonly FlowFixtureKind[] = ['noData', 'healthyWeek', 'reviewJam', 'gateJam'];
export const FLOW_FIXTURE_NOW = Date.parse('2026-11-02T12:00:00Z');

const ASPECTS = ['aspect-requirement-fit', 'aspect-code-quality', 'aspect-documentation-impact', 'aspect-tests-and-evidence'];

export function economicsRow(project: string, overrides: Partial<GateEconomicsRow> = {}): GateEconomicsRow {
  return {
    project, gatedMerges: 0, gateGreen: 0, gateRed: 0, gateCancelled: 0,
    gateMinutesMedian: null, gateMinutesP90: null, queueWaitP90Ms: null,
    exactReuseRate: null, disjointReuseRate: null, fullSuiteShare: null, sameShaRetestRate: null,
    lastNightlyGuard: null, deliveryLagP50Minutes: null, deliveryLagP95Minutes: null,
    oldestWaitingMinutes: null, preparationP50Minutes: null,
    queued: 0, prepared: 0, gating: 0, readyToPublish: 0,
    laneGateDurationP50Minutes: null, laneGateDurationP95Minutes: null,
    laneGateGreen: 0, laneGateRed: 0, laneGateCancelled: 0,
    gateTokenWaitP50Ms: null, gateTokenWaitP95Ms: null, gateHoldingProject: null,
    mergeLockWaitP95Ms: null, publicationLockWaitP95Ms: null,
    ejectedConflict: 0, ejectedAgentRound: 0, ejectedRed: 0, ejectedError: 0,
    arrivalsPerHour: 0, mergesPerHour: 0, projectShortCode: null, projectColor: null,
    baseline: null, batchesClosed: 0, meanBatchSize: null, soloGatedMerges: 0,
    ...overrides,
  };
}

function baseline(fullSuiteRuns: number, gateMinutesPerCardMedian: number) {
  return {
    leadTimeMedianMinutes: null, leadTimeP90Minutes: null, leadTimeCards: 0, leadTimeUnknownCards: 0,
    gateMinutesPerCardMedian, gateMinutesPerCardP90: null, gateMinutesCards: 1, gateMinutesUnknownCards: 0,
    fullSuiteRuns, fullSuiteRunsPerDay: null, redDevelopMedianMinutes: null, redDevelopP90Minutes: null,
    gateSourceBytesMedian: null, gateSourceBytesP90: null, gateSourceSecondsMedian: null,
    gateSourceSecondsP90: null, gateSourceRuns: 0,
  };
}

export function cycleTask(id: string, overrides: Partial<TaskCycleTimeRow> = {}): TaskCycleTimeRow {
  return {
    taskId: id, taskKey: id, title: `Card ${id}`, terminalState: '6-completed', watchPath: '/w',
    createdAt: '2026-10-30T08:00:00Z', firstClaimedAt: '2026-10-30T08:10:00Z', completedAt: '2026-11-01T08:00:00Z',
    completionSource: 'ledger',
    stages: {
      preparation: 0, queueWait: 0, coding: 0, reviewWait: 0, testGate: 0, reviewOther: 0,
      integration: 0, humanReview: 0, unattributed: 0,
    },
    reviewRunSeconds: 900, leadTimeSeconds: 7200, cycleTimeSeconds: 6600,
    codingRuns: 1, reviewRounds: 1, bounceRounds: 0, integrationAttempts: 1,
    integrationOutcome: 'Merged', integrationStage: 'pre-human-review', dataGaps: [],
    backwardTransitions: 0, cardTokens: 1_200_000, cardCostUsd: 2.4,
    reviewAspectSeconds: Object.fromEntries(ASPECTS.map(aspect => [aspect, 180])),
    ...overrides,
  };
}

export function cycleTime(project: string, window: FlowWindow, tasks: TaskCycleTimeRow[]): ProjectCycleTimeResponse {
  return {
    project, projectId: 'PROJ-001', shortCode: 'DEM', window, capturedAt: new Date(FLOW_FIXTURE_NOW).toISOString(),
    since: null,
    coverage: {
      tasksInProject: tasks.length, tasksTerminal: tasks.length, tasksInWindow: tasks.length,
      excludedNoCompletionTimestamp: 0, excludedInFlight: 0, tasksWithoutLedger: 0,
      tasksWithLaneEntryCompletion: 0, tasksBackfilled: 0,
    },
    aggregates: [], integrationOutcomes: [],
    transitions: {
      totalTransitions: 0, backwardTransitions: tasks.reduce((sum, task) => sum + task.backwardTransitions, 0),
      tasksWithBackwardTransitions: tasks.filter(task => task.backwardTransitions > 0).length,
      lanes: [], cells: [], laneDwell: [], bounceCauses: [], topLoops: [],
    },
    tasks,
  };
}

/** Lane drain with `[lane, count, oldest minutes]` per observed lane. */
export function pipelineHealth(project: string, nowMs: number,
  lanes: readonly [string, number, number | null][]): PipelineHealthSnapshot {
  return {
    project, capturedAtUtc: new Date(nowMs).toISOString(), status: 'healthy', activeGate: null, fingerprint: null,
    lanes: lanes.map(([lane, queueCount, oldest]) => ({
      lane, queueCount, completedPerHour: 1, isStalled: false,
      oldestQueuedAtUtc: oldest == null ? null : new Date(nowMs - oldest * 60_000).toISOString(),
    })),
    alerts: [],
  };
}

function economics(window: FlowWindow, project: string, row: Partial<GateEconomicsRow>,
  all: Partial<GateEconomicsRow> = row): GateEconomicsResponse {
  return {
    window, asOfUtc: new Date(FLOW_FIXTURE_NOW).toISOString(),
    rows: [economicsRow('All projects', all), economicsRow(project, row)],
  };
}

function report(project: string, closedBatches: number): BatchGateReport {
  return {
    project, closedBatches, eligibleDeliveredMembers: closedBatches * 3, fullSuiteRuns: closedBatches,
    meanBatchSize: closedBatches ? 2.6 : null, batchGreenRate: closedBatches ? 0.9 : null,
    constructionEjectionRate: closedBatches ? 0.05 : null,
  };
}

const HEALTHY_WEEK: Partial<GateEconomicsRow> = {
  gatedMerges: 21, batchesClosed: 7, meanBatchSize: 2.6, soloGatedMerges: 3,
  baseline: baseline(7, 8), exactReuseRate: 0.25, disjointReuseRate: 0.15,
  queued: 1, oldestWaitingMinutes: 15,
};
const HEALTHY_MONTH: Partial<GateEconomicsRow> = {
  gatedMerges: 80, batchesClosed: 26, meanBatchSize: 2.5, soloGatedMerges: 15,
  baseline: baseline(30, 9), exactReuseRate: 0.2, disjointReuseRate: 0.15,
};
const ALL_WEEK: Partial<GateEconomicsRow> = {
  gatedMerges: 40, batchesClosed: 12, meanBatchSize: 2.9, soloGatedMerges: 6,
  baseline: baseline(14, 9), exactReuseRate: 0.3, disjointReuseRate: 0.1,
};

function healthyTasks(prefix: string, count: number, backward: number): TaskCycleTimeRow[] {
  return Array.from({ length: count }, (_, index) => cycleTask(`${prefix}-${index + 1}`, {
    reviewRounds: index === 0 ? 2 : 1,
    backwardTransitions: index < backward ? 1 : 0,
  }));
}

/** The responses for one fixture situation, for the given project name. */
export function flowHealthFixture(kind: FlowFixtureKind, project = 'Demo', nowMs = FLOW_FIXTURE_NOW): FlowHealthResponses {
  if (kind === 'noData') {
    return {
      economics: { '7d': economics('7d', project, {}), '30d': economics('30d', project, {}) },
      cycleTime: { '7d': cycleTime(project, '7d', []), '30d': cycleTime(project, '30d', []) },
      pipelineHealth: pipelineHealth(project, nowMs,
        [['2-ready', 0, null], ['3-progress', 0, null], ['4-auto-review', 0, null], ['5-human-review', 0, null]]),
      batchReport: report(project, 0), batchingEnabled: true,
      gateSlots: { busy: 0, total: 2 }, reviewSlots: { busy: 0, total: 12 }, codingSlots: { busy: 0, total: 8 },
    };
  }
  const healthy: FlowHealthResponses = {
    economics: {
      '7d': economics('7d', project, HEALTHY_WEEK, ALL_WEEK),
      '30d': economics('30d', project, HEALTHY_MONTH),
    },
    cycleTime: {
      '7d': cycleTime(project, '7d', healthyTasks('w', 6, 1)),
      '30d': cycleTime(project, '30d', healthyTasks('m', 24, 5)),
    },
    pipelineHealth: pipelineHealth(project, nowMs,
      [['2-ready', 2, 30], ['3-progress', 3, 20], ['4-auto-review', 1, 20], ['5-human-review', 1, 180]]),
    batchReport: report(project, 31), batchingEnabled: true,
    gateSlots: { busy: 1, total: 2 }, reviewSlots: { busy: 4, total: 12 }, codingSlots: { busy: 5, total: 8 },
  };
  if (kind === 'healthyWeek') return healthy;
  if (kind === 'reviewJam') {
    return {
      ...healthy,
      pipelineHealth: pipelineHealth(project, nowMs,
        [['2-ready', 3, 40], ['3-progress', 2, 30], ['4-auto-review', 14, 360], ['5-human-review', 1, 120]]),
      reviewSlots: { busy: 3, total: 12 },
    };
  }
  const jammedWeek: Partial<GateEconomicsRow> = {
    ...HEALTHY_WEEK, baseline: baseline(21, 26), queued: 6, prepared: 1, gating: 2,
    oldestWaitingMinutes: 190, exactReuseRate: 0.05, disjointReuseRate: 0,
  };
  return {
    ...healthy,
    economics: { '7d': economics('7d', project, jammedWeek, ALL_WEEK), '30d': healthy.economics['30d'] },
    gateSlots: { busy: 2, total: 2 },
  };
}

/** A valid `GET /batch-gate` settings response with the given switch. */
export function batchSettingsPayload(enabled: boolean) {
  return {
    settings: {
      enabled, scope: 'code', planner: 'look-ahead', minimumSize: 2, maximumSize: 8,
      deadlineMinutes: 30, lookAheadMinutes: 45, urgentTags: [], riskPolicy: 'balanced', calibration: 'empirical', stacking: 'off',
      overlap: 'adjacent', reviewTestLevel: 'work-package', gateTimeoutMinutes: 90, preCheckEnabled: true,
      advisor: { mode: 'off', cliType: 'claude', model: null, timeoutSeconds: 60 },
    },
    limits: { hardMaximumSize: 12 },
  };
}

/** `GET /api/system/execution-infrastructure` carrying the fixture's gate slots. */
export function executionInfrastructurePayload(responses: FlowHealthResponses) {
  return { gate: { host: 'gate-host', transport: 'pull', busy: responses.gateSlots?.busy ?? 0, total: responses.gateSlots?.total ?? 0 } };
}
