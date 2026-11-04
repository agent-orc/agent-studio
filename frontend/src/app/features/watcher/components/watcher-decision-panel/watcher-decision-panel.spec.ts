import { describe, expect, it } from 'vitest';
import { watcherOutcomeTone } from './watcher-decision-panel';

describe('watcherOutcomeTone', () => {
  it('maps every recorded decision and falls back to neutral', () => {
    expect(['approved', 'edited', 'merged', 'rejected', 'something-new'].map(watcherOutcomeTone))
      .toEqual(['success', 'success', 'info', 'neutral', 'neutral']);
  });
});
