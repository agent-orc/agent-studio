import { describe, expect, it } from 'vitest';
import { liveRunInclusionTone, usageStateTone } from './usage-detail-status';

describe('usage detail status tones', () => {
  it('maps every usage data state, unknown values stay neutral', () => {
    expect(usageStateTone('normal')).toBe('success');
    expect(usageStateTone('loading')).toBe('running');
    expect(usageStateTone('stale')).toBe('warning');
    expect(usageStateTone('suspicious')).toBe('warning');
    expect(usageStateTone('partial')).toBe('warning');
    expect(usageStateTone('unknown')).toBe('neutral');
    expect(usageStateTone('later')).toBe('neutral');
    expect(usageStateTone(null)).toBe('neutral');
  });

  it('maps live run inclusion', () => {
    expect(liveRunInclusionTone(true)).toBe('success');
    expect(liveRunInclusionTone(false)).toBe('neutral');
  });
});
