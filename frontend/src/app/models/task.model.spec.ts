import { describe, expect, it } from 'vitest';
import { boardLanes, type GroupedJobs, type TaskInfo } from './task.model';

function card(id: string, state: string): TaskInfo {
  return { id, taskKey: `ws::${id}`, title: id, state } as TaskInfo;
}

function grouped(overrides: Partial<GroupedJobs> = {}): GroupedJobs {
  return {
    backlog: [],
    preparation: [],
    orchestratorPrep: [],
    ready: [],
    progress: [],
    failedPickup: [],
    codeNotComplete: [],
    autoReview: [],
    humanReview: [],
    escalated: [],
    review: [],
    completed: [],
    archive: [],
    ...overrides,
  } as GroupedJobs;
}

describe('boardLanes (AGT-2726)', () => {
  it('returns every lane array', () => {
    const lanes = boardLanes(grouped({
      ready: [card('a', '2-ready')],
      progress: [card('b', '3-progress')],
    }));
    expect(lanes.flat().map((t) => t.id)).toEqual(['a', 'b']);
  });

  it('skips the scalar git-state stamp fields', () => {
    // The regression this exists for: the grouped payload gained
    // `gitStateAt` (a string) and `gitStateStale` (a boolean). Walking
    // Object.values and casting each value to TaskInfo[] threw on the boolean
    // and produced one bogus "card" per character of the timestamp.
    const lanes = boardLanes(grouped({
      ready: [card('a', '2-ready')],
      gitStateAt: '2026-09-07T10:00:00Z',
      gitStateStale: true,
    }));

    expect(lanes).toHaveLength(13);
    expect(lanes.flat()).toHaveLength(1);
    expect(lanes.flat()[0].id).toBe('a');
  });

  it('tolerates a stale client shape whose lanes are missing', () => {
    const lanes = boardLanes({ ready: [card('a', '2-ready')] } as unknown as GroupedJobs);
    expect(lanes.flat().map((t) => t.id)).toEqual(['a']);
  });
});
