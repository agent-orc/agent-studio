import { describe, expect, it } from 'vitest';
import { FLOW_FIGURE_TARGETS } from '../models/flow-health-targets';
import type { FlowHealthView } from '../models/flow-health.model';
import { FLOW_FIXTURE_NOW, cycleTask, cycleTime, economicsRow, flowHealthFixture, type FlowFixtureKind } from '../testing/flow-health.fixtures';
import { deriveFlowHealth, formatAge, formatTarget, joinFlowSources, trend } from './flow-health.derive';

function view(kind: FlowFixtureKind, project = 'Demo'): FlowHealthView {
  return deriveFlowHealth(joinFlowSources(project, FLOW_FIXTURE_NOW, flowHealthFixture(kind, project)));
}

function figure(result: FlowHealthView, id: string) {
  const found = result.blocks.flatMap(block => block.figures).find(item => item.id === id);
  if (!found) throw new Error(`missing figure ${id}`);
  return found;
}

function stage(result: FlowHealthView, key: string) {
  const found = result.now.find(item => item.stage === key);
  if (!found) throw new Error(`missing stage ${key}`);
  return found;
}

describe('flow health derivation', () => {
  it('reports no data without inventing zeros', () => {
    const result = view('noData');

    expect(result.brake).toEqual({
      stage: null, figure: null, tone: 'empty',
      sentence: 'No flow data yet: nothing waits and no card was delivered in the last 7 days',
    });
    for (const id of ['batchesPerDay', 'meanBatchSize', 'soloShare', 'fullSuiteRunsPerCard',
      'gateMinutesPerCard', 'reuseRate', 'reviewRoundsPerCard', 'tokensPerCard', 'backwardShare']) {
      expect(figure(result, id).value7d, id).toBeNull();
      expect(figure(result, id).status, id).toBe('unknown');
    }
    expect(result.now.every(item => item.waiting === 0 && item.reason === 'Nothing waiting')).toBe(true);
    expect(result.batchLifetime).toBe('No batch has closed yet.');
  });

  it('meets every target in a healthy week and names no brake', () => {
    const result = view('healthyWeek');

    expect(result.brake.tone).toBe('calm');
    expect(result.brake.sentence).toBe('No brake right now: every measured stage and figure is within its target');
    const figures = result.blocks.flatMap(block => block.figures);
    expect(figures.filter(item => item.status !== 'met').map(item => item.id)).toEqual([]);
    expect(figure(result, 'batchesPerDay').value7d).toBe(1);
    expect(figure(result, 'soloShare').value7d).toBeCloseTo(3 / 21);
    expect(figure(result, 'fullSuiteRunsPerCard').value7d).toBeCloseTo(7 / 21);
    expect(figure(result, 'reuseRate').value7d).toBeCloseTo(0.4);
    expect(figure(result, 'gateSlotsBusy').value7d).toBe(0.5);
    expect(figure(result, 'reviewRoundsPerCard').value7d).toBeCloseTo(7 / 6);
    expect(figure(result, 'reviewMinutesPerCard').value7d).toBe(15);
    expect(figure(result, 'aspect:aspect-code-quality').value7d).toBe(3);
    expect(figure(result, 'aspect:aspect-code-quality').label).toBe('Review minutes per card: Code quality');
    expect(figure(result, 'tokensPerCard').value7d).toBe(1_200_000);
    expect(figure(result, 'backwardShare').note).toBe('1 of 6 delivered cards in 7 days.');
    expect(result.batchLifetime).toBe('Since batching started: 31 batches, mean 2.6 cards, 90% green.');
  });

  it('carries the all-projects row for figures the gate projection aggregates', () => {
    const result = view('healthyWeek');

    expect(figure(result, 'meanBatchSize').all7d).toBe(2.9);
    expect(figure(result, 'fullSuiteRunsPerCard').all7d).toBeCloseTo(14 / 40);
    // Review figures come from the per-project cycle-time projection only.
    expect(figure(result, 'reviewRoundsPerCard').all7d).toBeUndefined();
  });

  it('names a review jam with its count, oldest age and slot use', () => {
    const result = view('reviewJam');

    expect(result.brake).toEqual({
      stage: 'review', figure: null, tone: 'brake',
      sentence: 'Review waits: 14 cards, oldest 6 h, 3 of 12 slots busy',
    });
    expect(stage(result, 'review')).toMatchObject({
      waiting: 14, status: 'missed', reason: '3 of 12 review slots busy', lane: '4-auto-review',
    });
    expect(stage(result, 'pickup').status).toBe('met');
  });

  it('names a gate jam over a review backlog that is still within target', () => {
    const result = view('gateJam');

    expect(result.brake.sentence).toBe('Gate waits: 9 cards, oldest 3 h, 2 of 2 slots busy');
    expect(stage(result, 'gate')).toMatchObject({ waiting: 9, reason: 'All 2 gate slots busy', lane: null });
    expect(figure(result, 'gateMinutesPerCard')).toMatchObject({ value7d: 26, status: 'missed', trend: 'worse' });
    expect(figure(result, 'gateSlotsBusy')).toMatchObject({ value7d: 1, status: 'missed' });
  });

  it('names the project holding the shared gate', () => {
    const responses = flowHealthFixture('gateJam');
    const week = responses.economics['7d']!;
    const rows = week.rows.map(row => row.project === 'Demo' ? { ...row, gateHoldingProject: 'Other' } : row);
    const result = deriveFlowHealth(joinFlowSources('Demo', FLOW_FIXTURE_NOW,
      { ...responses, economics: { ...responses.economics, '7d': { ...week, rows } } }));

    expect(stage(result, 'gate').reason).toBe('Gate held by Other');
    expect(result.brake.sentence).toBe('Gate waits: 9 cards, oldest 3 h, 2 of 2 slots busy, gate held by Other');
  });

  it('falls back to the figure furthest from its target when no queue is over target', () => {
    const responses = flowHealthFixture('healthyWeek');
    const slow = [cycleTask('a', { reviewRunSeconds: 3600 }), cycleTask('b', { reviewRunSeconds: 3600 })];
    const result = deriveFlowHealth(joinFlowSources('Demo', FLOW_FIXTURE_NOW, {
      ...responses, cycleTime: { ...responses.cycleTime, '7d': cycleTime('Demo', '7d', slow) },
    }));

    expect(result.brake).toEqual({
      stage: null, figure: 'reviewMinutesPerCard', tone: 'brake',
      sentence: 'No queue over its target; review minutes per card is 60 min against a target of ≤ 20 min',
    });
  });

  it('marks grouping not applicable while the batch gate is off', () => {
    const result = deriveFlowHealth(joinFlowSources('Demo', FLOW_FIXTURE_NOW,
      { ...flowHealthFixture('healthyWeek'), batchingEnabled: false }));

    const grouping = result.blocks.find(block => block.key === 'grouping')!;
    expect(grouping.figures.every(item => item.status === 'not-applicable')).toBe(true);
    expect(grouping.figures[0].note).toBe('The batch gate is off for this project.');
  });

  it('ignores backfilled rows, which date a completion but measure no review', () => {
    const responses = flowHealthFixture('healthyWeek');
    const tasks = [cycleTask('a', { reviewRounds: 1 }), cycleTask('b', { reviewRounds: 9, stages: null })];
    const result = deriveFlowHealth(joinFlowSources('Demo', FLOW_FIXTURE_NOW, {
      ...responses, cycleTime: { ...responses.cycleTime, '7d': cycleTime('Demo', '7d', tasks) },
    }));

    expect(figure(result, 'reviewRoundsPerCard').value7d).toBe(1);
  });

  it('matches the project row case-insensitively and keeps the all-projects row apart', () => {
    const sources = joinFlowSources('demo', FLOW_FIXTURE_NOW, {
      ...flowHealthFixture('healthyWeek'),
      economics: {
        '7d': { window: '7d', asOfUtc: '', rows: [economicsRow('All projects', { gatedMerges: 9 }), economicsRow('Demo', { gatedMerges: 4 })] },
        '30d': null,
      },
    });

    expect(sources.windows['7d'].economics?.gatedMerges).toBe(4);
    expect(sources.windows['7d'].economicsAll?.gatedMerges).toBe(9);
    expect(sources.windows['30d'].economics).toBeNull();
  });
});

describe('flow health formatting', () => {
  it('reads trends in the direction of the target', () => {
    const lower = FLOW_FIGURE_TARGETS.gateMinutesPerCard;
    const higher = FLOW_FIGURE_TARGETS.reuseRate;
    expect(trend(8, 10, lower)).toBe('better');
    expect(trend(12, 10, lower)).toBe('worse');
    expect(trend(10.5, 10, lower)).toBe('steady');
    expect(trend(0.5, 0.3, higher)).toBe('better');
    expect(trend(null, 10, lower)).toBe('unknown');
    expect(trend(0, 0, lower)).toBe('steady');
  });

  it('states targets and ages in readable units', () => {
    expect(formatTarget(FLOW_FIGURE_TARGETS.gateMinutesPerCard)).toBe('≤ 10 min');
    expect(formatTarget(FLOW_FIGURE_TARGETS.meanBatchSize)).toBe('≥ 2 cards');
    expect(formatTarget(FLOW_FIGURE_TARGETS.soloShare)).toBe('≤ 30%');
    expect(formatTarget(FLOW_FIGURE_TARGETS.costPerCard)).toBe('≤ $5.00');
    expect(formatAge(45)).toBe('45 min');
    expect(formatAge(360)).toBe('6 h');
    expect(formatAge(3 * 1440)).toBe('3 d');
    expect(formatAge(null)).toBe('unknown');
  });
});
