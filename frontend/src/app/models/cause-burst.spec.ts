import { describe, expect, it } from 'vitest';
import { CAUSE_BURST_WINDOW_MS, groupCauseBursts } from './cause-burst';

interface Item { id: string; cause: string | null; at: string }

const at = (minute: number) => new Date(Date.UTC(2026, 10, 9, 9, minute)).toISOString();
const group = (items: Item[]) => groupCauseBursts(items, item => item.cause, item => Date.parse(item.at));

describe('groupCauseBursts (AGT-3585)', () => {
  it('folds a burst of two causes into two groups whose sizes add up to every item', () => {
    const items: Item[] = [
      { id: 'a', cause: 'stall', at: at(23) },
      { id: 'b', cause: 'missing-delivery', at: at(23) },
      { id: 'c', cause: 'stall', at: at(23) },
      { id: 'd', cause: 'missing-delivery', at: at(24) },
      { id: 'e', cause: 'missing-delivery', at: at(24) },
    ];
    const bursts = group(items);
    expect(bursts.map(burst => [burst.cause, burst.members.map(item => item.id)])).toEqual([
      ['stall', ['a', 'c']],
      ['missing-delivery', ['b', 'd', 'e']],
    ]);
    expect(bursts.reduce((sum, burst) => sum + burst.members.length, 0)).toBe(items.length);
  });

  it('chains members within the window and starts a new burst after a longer gap', () => {
    const windowMinutes = CAUSE_BURST_WINDOW_MS / 60_000;
    const bursts = group([
      { id: 'a', cause: 'stall', at: at(0) },
      { id: 'b', cause: 'stall', at: at(windowMinutes) },
      { id: 'c', cause: 'stall', at: at(windowMinutes * 2) },
      { id: 'd', cause: 'stall', at: at(windowMinutes * 3 + 1) },
    ]);
    expect(bursts.map(burst => burst.members.map(item => item.id))).toEqual([['a', 'b', 'c'], ['d']]);
    expect(bursts[0].lastMs - bursts[0].firstMs).toBe(CAUSE_BURST_WINDOW_MS * 2);
  });

  it('keeps an item without a cause or time on its own', () => {
    const bursts = group([
      { id: 'a', cause: null, at: at(1) },
      { id: 'b', cause: null, at: at(1) },
      { id: 'c', cause: 'stall', at: 'not a time' },
    ]);
    expect(bursts.map(burst => burst.members.map(item => item.id))).toEqual([['a'], ['b'], ['c']]);
    expect(bursts.every(burst => burst.cause === null)).toBe(true);
  });

  it('keeps the burst key while newer members join', () => {
    const before = group([{ id: 'a', cause: 'stall', at: at(1) }, { id: 'b', cause: 'stall', at: at(2) }]);
    const after = group([
      { id: 'c', cause: 'stall', at: at(3) },
      { id: 'a', cause: 'stall', at: at(1) },
      { id: 'b', cause: 'stall', at: at(2) },
    ]);
    expect(after[0].key).toBe(before[0].key);
  });
});
