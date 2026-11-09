import { describe, expect, it } from 'vitest';
import type { PickupHoldStatus, TaskInfo, TaskLiveQueue } from './task.model';
import { readyWaitReason, runnerWaitHeadline, splitReadyWaits } from './ready-wait-reason';

function ready(id: string, overrides: Partial<TaskInfo> = {}): TaskInfo {
  return {
    id, key: id.toUpperCase(), taskKey: `demo::${id}`, title: id, state: '2-ready', order: 1, agent: 'codex',
    createdAt: '2026-11-09T09:00:00Z', watchPath: '/w', projectName: 'demo', folderPath: `/w/2-ready/${id}`,
    lastActivity: '2026-11-09T09:00:00Z', sessionName: null, model: null, cliType: 'codex', useOwnSession: null,
    lastUsage: null, execution: null, commit: null,
    ...overrides,
  };
}

function queued(id: string, queue: TaskLiveQueue): TaskInfo {
  return ready(id, { liveStatus: { attempt: 1, activeStep: null, nextSteps: [], queue } });
}

const hold = (mechanism: string): PickupHoldStatus => ({
  mechanism, reason: 'held', sinceUtc: '2026-11-09T09:00:00Z', unsatisfiable: false, resolutions: [],
});

describe('readyWaitReason (AGT-3579)', () => {
  it('ranks a blocked dependency, then a hold, then the runner reason', () => {
    const runner = { kind: 'runner', position: 1, reasonCode: 'quota-defer', reason: 'quota' };
    const blocked = { blocked: true, cycleDetected: false, items: [] };

    expect(readyWaitReason(queued('a', runner))).toEqual({ kind: 'runner', code: 'quota-defer', label: 'quota defer' });
    expect(readyWaitReason({ ...queued('b', runner), pickupHold: hold('crash-backoff') }))
      .toEqual({ kind: 'hold', code: 'crash-backoff', label: 'crash cooldown' });
    expect(readyWaitReason({ ...queued('c', runner), pickupHold: hold('crash-backoff'), waitsOn: blocked }).code)
      .toBe('dependency-gate');
    expect(readyWaitReason(queued('d', { kind: 'runner', position: 1 })))
      .toEqual({ kind: 'queued', code: 'queued', label: 'queued' });
  });

  it('reads an unknown runner code instead of dropping the card', () => {
    expect(readyWaitReason(queued('a', { kind: 'runner', reasonCode: 'new-gate' })).label).toBe('new gate');
    expect(runnerWaitHeadline('new-gate', 3)).toBe('Not picked · new gate · position 3');
  });
});

describe('splitReadyWaits (AGT-3579, R3)', () => {
  it('puts every card in exactly one bucket, so the split sums to the lane count', () => {
    const cards = [
      queued('a', { kind: 'runner', position: 1, reasonCode: 'manual-mode' }),
      queued('b', { kind: 'runner', position: 2, reasonCode: 'manual-mode' }),
      { ...queued('c', { kind: 'runner', position: 3, reasonCode: 'manual-mode' }), pickupHold: hold('dependency-gate') },
      queued('d', { kind: 'runner', position: 4, reasonCode: 'quota-defer' }),
      queued('e', { kind: 'runner', position: 5 }),
      ready('f', { liveStatus: null }),
    ];

    const buckets = splitReadyWaits(cards);

    expect(buckets.map(bucket => [bucket.kind, bucket.code, bucket.count])).toEqual([
      ['hold', 'dependency-gate', 1],
      ['runner', 'manual-mode', 2],
      ['runner', 'quota-defer', 1],
      ['queued', 'queued', 2],
    ]);
    expect(buckets.reduce((sum, bucket) => sum + bucket.count, 0)).toBe(cards.length);
    expect(buckets[1].keys).toEqual(['A', 'B']);
  });

  it('counts a client-side hold the caller derives', () => {
    const cards = [queued('a', { kind: 'runner', reasonCode: 'manual-mode' })];
    expect(splitReadyWaits(cards, () => hold('claim-load-gate'))[0])
      .toMatchObject({ kind: 'hold', code: 'claim-load-gate', label: 'host load', count: 1 });
  });
});
