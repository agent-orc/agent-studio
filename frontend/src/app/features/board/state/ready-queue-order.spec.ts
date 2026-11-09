import { describe, expect, it } from 'vitest';
import type { TaskInfo } from '../../../models/task.model';
import { readyQueueReorder } from './ready-queue-order';

function card(id: string, watchPath = '/p/a', extra: Partial<TaskInfo> = {}): TaskInfo {
  return { id, watchPath, state: '2-ready', ...extra } as TaskInfo;
}

const ids = (keys: { jobId: string }[] | undefined) => keys?.map((k) => k.jobId);

describe('readyQueueReorder (AGT-3588)', () => {
  const lane = [card('a1'), card('b1', '/p/b'), card('a2'), card('a3'), card('b2', '/p/b')];

  it('moves a card to the top of its own project queue and keeps other projects in their slots', () => {
    const r = readyQueueReorder(lane, card('a3'), 'top');
    expect(ids(r?.prevQueue)).toEqual(['a1', 'a2', 'a3']);
    expect(ids(r?.nextQueue)).toEqual(['a3', 'a1', 'a2']);
    expect(ids(r?.prevLane)).toEqual(['a1', 'b1', 'a2', 'a3', 'b2']);
    expect(ids(r?.nextLane)).toEqual(['a3', 'b1', 'a1', 'a2', 'b2']);
  });

  it('moves a card to the bottom of its own project queue', () => {
    const r = readyQueueReorder(lane, card('a1'), 'bottom');
    expect(ids(r?.nextQueue)).toEqual(['a2', 'a3', 'a1']);
    expect(ids(r?.nextLane)).toEqual(['a2', 'b1', 'a3', 'a1', 'b2']);
  });

  it('returns null at the edge already reached or for a card outside the lane', () => {
    expect(readyQueueReorder(lane, card('a1'), 'top')).toBeNull();
    expect(readyQueueReorder(lane, card('a3'), 'bottom')).toBeNull();
    expect(readyQueueReorder(lane, card('zz'), 'top')).toBeNull();
  });

  it('keeps pending-intent cards at the head like the backend promote', () => {
    const withIntent = [card('a1', '/p/a', { pendingIntent: { mode: 'continue' } as TaskInfo['pendingIntent'] }), card('a2'), card('a3')];
    expect(ids(readyQueueReorder(withIntent, card('a3'), 'top')?.nextQueue)).toEqual(['a1', 'a3', 'a2']);
    expect(readyQueueReorder(withIntent, card('a2'), 'top')).toBeNull();
  });
});
