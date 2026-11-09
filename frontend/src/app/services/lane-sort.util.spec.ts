import { describe, expect, it } from 'vitest';
import type { TaskInfo } from '../models/task.model';
import { isReadyQueueHead } from '../features/board/components/ready-lane-split.util';
import {
  READY_PICKUP_RULE,
  laneSortIndicator,
  perProjectOrderNote,
  sortLaneLikeBoard,
} from './lane-sort.util';

function card(id: string, projectName: string, order: number, extra: Partial<TaskInfo> = {}): TaskInfo {
  return {
    id,
    key: id.toUpperCase(),
    projectName,
    order,
    state: '2-ready',
    createdAt: '2026-11-01T09:00:00Z',
    lastActivity: '2026-11-01T09:00:00Z',
    enteredLaneAt: '2026-11-01T09:00:00Z',
    ...extra,
  } as TaskInfo;
}

const ids = (jobs: readonly TaskInfo[]) => jobs.map((job) => job.id);

describe('sortLaneLikeBoard (AGT-3587)', () => {
  it('groups projects alphabetically instead of sorting the lane by order across projects', () => {
    // The walkthrough case: DEMO-55 (order 50) sorted after PLAT-2 (order 200)
    // once a pushed card re-sorted the whole lane by `order`.
    const lane = [
      card('plat-2', 'Demo Platform', 200),
      card('demo-55', 'Demo App', 50),
      card('plat-16', 'Demo Platform', 10),
      card('demo-3', 'Demo App', 300),
    ];

    expect(ids(sortLaneLikeBoard(lane, '2-ready', {}))).toEqual(['demo-55', 'demo-3', 'plat-16', 'plat-2']);
  });

  it('keeps placed cards first and the newest unplaced entry on top under lane-entry', () => {
    const lane = [
      card('old-entry', 'P', 999, { enteredLaneAt: '2026-11-01T09:00:00Z' }),
      card('placed', 'P', 40),
      card('new-entry', 'P', 999, { enteredLaneAt: '2026-11-05T09:00:00Z' }),
    ];

    expect(ids(sortLaneLikeBoard(lane, '2-ready', { P: { '2-ready': 'lane-entry' } })))
      .toEqual(['placed', 'new-entry', 'old-entry']);
  });

  it('uses each project strategy, and the Ready strategy for the intake sub-lane', () => {
    const lane = [
      card('a-7', 'Alpha', 1),
      card('a-3', 'Alpha', 2),
      card('b-9', 'Bravo', 5),
      card('b-1', 'Bravo', 1),
    ];
    const strategies = { Alpha: { '2-ready': 'newest-first' }, Bravo: { '2-ready': 'manual' } };

    expect(ids(sortLaneLikeBoard(lane, '2-ready-intake', strategies))).toEqual(['a-7', 'a-3', 'b-1', 'b-9']);
  });

  it('moving one card leaves the relative order of the others intact', () => {
    const before = [card('d-1', 'D', 10), card('d-2', 'D', 20), card('d-3', 'D', 30), card('p-1', 'P', 10)];
    const after = sortLaneLikeBoard(
      [...before.filter((job) => job.id !== 'd-3'), { ...before[2], order: 0 }],
      '2-ready',
      {},
    );

    expect(ids(after)).toEqual(['d-3', 'd-1', 'd-2', 'p-1']);
  });
});

describe('laneSortIndicator (AGT-3587)', () => {
  it('states the pickup rule on Ready and the placed-first order under lane-entry', () => {
    const tooltip = laneSortIndicator('lane-entry', '2-ready')!.tooltip;

    expect(tooltip).toContain('Cards placed by hand first, then the most recently entered on top.');
    expect(tooltip).toContain(READY_PICKUP_RULE);
    expect(tooltip).not.toContain('—');
  });

  it('keeps other lanes free of the pickup rule', () => {
    expect(laneSortIndicator('manual', '0-backlog')!.tooltip).toBe('Manual order. Drag cards to reorder.');
    expect(laneSortIndicator('newest-first', '6-completed')!.tooltip)
      .toBe('Auto-sorted by newest first; switch this lane to Manual to reorder.');
    expect(laneSortIndicator('', '2-ready')).toBeNull();
  });

  it('says a mixed lane is sorted per project', () => {
    const mixed = laneSortIndicator('mixed', '2-ready')!;

    expect(mixed.icon).toBe('⇅');
    expect(mixed.tooltip).toMatch(/^Sorted per project/);
    expect(mixed.tooltip).toContain(READY_PICKUP_RULE);
  });
});

describe('perProjectOrderNote (AGT-3587)', () => {
  it('appears only on a Ready lane that mixes projects', () => {
    const mixed = [card('a', 'Alpha', 1), card('b', 'Bravo', 1)];

    expect(perProjectOrderNote('2-ready', mixed)).toBe('Grouped by project. Each project keeps its own order.');
    expect(perProjectOrderNote('2-ready', [mixed[0]])).toBeNull();
    expect(perProjectOrderNote('0-backlog', mixed)).toBeNull();
  });
});

describe('isReadyQueueHead (AGT-3587)', () => {
  const status = {
    projects: {
      Alpha: { queuedJobIds: ['a-2', 'a-1'] },
    },
  } as never;

  it('marks only the first queued card of its project', () => {
    expect(isReadyQueueHead(card('a-2', 'Alpha', 2), status)).toBe(true);
    expect(isReadyQueueHead(card('a-1', 'Alpha', 1), status)).toBe(false);
    expect(isReadyQueueHead(card('b-1', 'Bravo', 1), status)).toBe(false);
    expect(isReadyQueueHead(card('a-2', 'Alpha', 2, { state: '3-progress' }), status)).toBe(false);
  });
});
