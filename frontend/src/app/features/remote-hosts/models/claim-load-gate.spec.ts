import { describe, expect, it } from 'vitest';
import { claimLoadGateLabel } from './claim-load-gate';

describe('claimLoadGateLabel', () => {
  it('states why free slots cannot claim', () => {
    expect(claimLoadGateLabel({ state: 'closed', loadPerCore: 5.51, threshold: 4,
      sustainedSeconds: 702, since: '2026-11-05T12:00:00Z', activeSlots: 7 }))
      .toBe('Not claiming: load 5.5 per core above 4.0 for 12 min');
  });

  it('keeps the open state accurate during a rising load window', () => {
    expect(claimLoadGateLabel({ state: 'open', loadPerCore: 4.4, threshold: 4,
      sustainedSeconds: 0, since: null, activeSlots: 7 }))
      .toContain('waiting for sustained threshold');
  });
});
