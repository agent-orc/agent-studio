import { describe, expect, it } from 'vitest';
import type { CycleTimeAggregate, TaskCycleTimeRow } from '../../models/project-cycle-time.model';
import {
  compositionSegments,
  formatCount,
  formatDuration,
  formatTimestamp,
  laneLabel,
  matrixLevel,
  windowLabel,
} from './cycle-time-format';
import { CycleTimeTableState, sortValue } from './cycle-time-table-state';

describe('cycle-time formatting', () => {
  it('formats durations with one unit and at most one decimal', () => {
    expect(formatDuration(0)).toBe('0s');
    expect(formatDuration(42)).toBe('42s');
    expect(formatDuration(90)).toBe('1.5m');
    expect(formatDuration(1800)).toBe('30m');
    expect(formatDuration(7560)).toBe('2.1h');
    expect(formatDuration(86_400 * 1.25)).toBe('1.3d');
    // Unit thresholds apply after rounding: no "60s", "60m", or "24h".
    expect(formatDuration(59.4)).toBe('59s');
    expect(formatDuration(59.6)).toBe('1m');
    expect(formatDuration(3599)).toBe('1h');
    expect(formatDuration(3570)).toBe('59.5m');
    expect(formatDuration(86_399)).toBe('1d');
    expect(formatDuration(86_040)).toBe('23.9h');
    expect(formatDuration(null)).toBeNull();
    expect(formatDuration(undefined)).toBeNull();
    expect(formatDuration(Number.NaN)).toBeNull();
  });

  it('formats counts and window labels', () => {
    expect(formatCount(3)).toBe('3');
    expect(formatCount(3.75)).toBe('3.8');
    expect(formatCount(null)).toBeNull();
    expect(windowLabel('7d')).toBe('Last 7 days');
    expect(windowLabel('30d')).toBe('Last 30 days');
    expect(windowLabel('all')).toBe('All time');
  });

  it('formats timestamps as local date and minute, tolerating junk', () => {
    const iso = new Date(2026, 7, 21, 6, 44, 59).toISOString();
    expect(formatTimestamp(iso)).toBe('2026-08-21 06:44');
    expect(formatTimestamp('not-a-date')).toBe('not-a-date');
    expect(formatTimestamp(null)).toBe('');
  });

  it('labels lanes and bands matrix counts on a square-root scale', () => {
    expect(laneLabel('4-auto-review')).toBe('Post Processing');
    expect(laneLabel('5e-escalated')).toBe('Escalated');
    expect(laneLabel('')).toBe('unknown');
    // AGT-2715: an unknown lane degrades to a readable name rather than
    // leaking its raw key into a matrix header.
    expect(laneLabel('9-custom')).toBe('Custom');

    expect(matrixLevel(0, 100)).toBe(0);
    expect(matrixLevel(5, 0)).toBe(0);
    expect(matrixLevel(1, 100)).toBe(1);   // sqrt ratio 0.1 -> band 1
    expect(matrixLevel(25, 100)).toBe(2);  // 0.5 -> band 2
    expect(matrixLevel(36, 100)).toBe(3);  // 0.6 -> band 3
    expect(matrixLevel(100, 100)).toBe(4);
    expect(matrixLevel(400, 100)).toBe(4);
  });

  it('builds composition segments from stage medians in lane order and skips empty stages', () => {
    const aggregates: CycleTimeAggregate[] = [
      aggregate('queueWait', 'Queue wait', 'stage', 600, 12),
      aggregate('coding', 'Coding run', 'stage', 1800, 12),
      aggregate('preparation', 'Preparation', 'stage', null, 0),
      aggregate('testGate', 'Build/test gate', 'stage', 600, 9, true),
      aggregate('leadTime', 'Lead time', 'rollup', 9000, 12),
      aggregate('codingRuns', 'Coding runs', 'count', 2, 12),
    ];

    const segments = compositionSegments(aggregates);

    expect(segments.map(s => s.stage)).toEqual(['queueWait', 'coding', 'testGate']);
    expect(segments.map(s => Math.round(s.percent))).toEqual([20, 60, 20]);
    expect(segments[2].highlighted).toBe(true);
    expect(segments[2].count).toBe(9);
    expect(compositionSegments([])).toEqual([]);
  });
});

describe('CycleTimeTableState', () => {
  const rows: TaskCycleTimeRow[] = [
    row('AGT-10', '2026-08-20T10:00:00Z', 3600, 120, 'Merged'),
    row('AGT-2', '2026-08-21T10:00:00Z', 7200, 0, null),
    row('AGT-7', '2026-08-19T10:00:00Z', 1800, 900, 'Conflict'),
  ];

  it('defaults to newest completion first and toggles direction on the same column', () => {
    const state = new CycleTimeTableState();
    expect(state.sort(rows).map(r => r.taskKey)).toEqual(['AGT-2', 'AGT-10', 'AGT-7']);

    state.selectSort('completedAt');
    expect(state.direction()).toBe('asc');
    expect(state.sort(rows).map(r => r.taskKey)).toEqual(['AGT-7', 'AGT-10', 'AGT-2']);
  });

  it('sorts by totals, by any stage, and by key with numeric collation', () => {
    const state = new CycleTimeTableState();
    state.selectSort('leadTime');
    expect(state.direction()).toBe('desc');
    expect(state.sort(rows).map(r => r.taskKey)).toEqual(['AGT-2', 'AGT-10', 'AGT-7']);

    state.selectSort('testGate');
    expect(state.sort(rows).map(r => r.taskKey)).toEqual(['AGT-7', 'AGT-10', 'AGT-2']);

    state.selectSort('key');
    expect(state.direction()).toBe('asc');
    expect(state.sort(rows).map(r => r.taskKey)).toEqual(['AGT-2', 'AGT-7', 'AGT-10']);

    state.selectSort('outcome');
    expect(state.sort(rows).map(r => r.taskKey)).toEqual(['AGT-2', 'AGT-7', 'AGT-10']);
  });

  it('keeps the incoming order for ties and treats unknown cycle time as smallest', () => {
    const state = new CycleTimeTableState();
    const tied = [row('A', '2026-08-20T10:00:00Z', 100, 5, null), row('B', '2026-08-20T10:00:00Z', 100, 5, null)];
    state.selectSort('leadTime');
    expect(state.sort(tied).map(r => r.taskKey)).toEqual(['A', 'B']);
    expect(sortValue({ ...tied[0], cycleTimeSeconds: null }, 'cycleTime')).toBe(-1);
  });
});

function aggregate(
  stage: string,
  label: string,
  kind: CycleTimeAggregate['kind'],
  p50: number | null,
  count: number,
  highlighted = false,
): CycleTimeAggregate {
  return {
    stage,
    label,
    kind,
    unit: kind === 'count' ? 'count' : 'seconds',
    highlighted,
    count,
    p50,
    p90: p50,
    max: p50,
    mean: p50,
    total: (p50 ?? 0) * count,
  };
}

function row(key: string, completedAt: string, lead: number, testGate: number, outcome: string | null): TaskCycleTimeRow {
  return {
    taskId: key.toLowerCase(),
    taskKey: key,
    title: `${key} title`,
    terminalState: '7-archive',
    watchPath: 'C:/tasks/demo',
    createdAt: '2026-08-18T00:00:00Z',
    firstClaimedAt: '2026-08-18T00:10:00Z',
    completedAt,
    completionSource: 'ledger',
    stages: {
      preparation: 0,
      queueWait: 60,
      coding: lead - 60 - testGate,
      reviewWait: 0,
      testGate,
      reviewOther: 0,
      integration: 0,
      humanReview: 0,
      unattributed: 0,
    },
    reviewRunSeconds: testGate,
    leadTimeSeconds: lead,
    cycleTimeSeconds: lead - 60,
    codingRuns: 1,
    reviewRounds: 1,
    bounceRounds: 0,
    integrationAttempts: outcome ? 1 : 0,
    integrationOutcome: outcome,
    integrationStage: outcome ? 'pre-human-review' : null,
    dataGaps: [],
    backwardTransitions: testGate > 600 ? 2 : 0,
    transitions: null,
  };
}
