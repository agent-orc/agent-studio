import { describe, expect, it } from 'vitest';
import { driftScoreBandTone } from './wiki-drift-band';

describe('driftScoreBandTone', () => {
  it('maps every score band onto a status tone', () => {
    expect(driftScoreBandTone('Healthy')).toBe('success');
    expect(driftScoreBandTone('Watch')).toBe('info');
    expect(driftScoreBandTone('Warn')).toBe('warning');
    expect(driftScoreBandTone('Critical')).toBe('danger');
    expect(driftScoreBandTone('Unknown')).toBe('neutral');
  });

  it('falls back to neutral for missing or unknown bands', () => {
    expect(driftScoreBandTone(null)).toBe('neutral');
    expect(driftScoreBandTone(undefined)).toBe('neutral');
    expect(driftScoreBandTone('Exploded')).toBe('neutral');
    expect(driftScoreBandTone('constructor')).toBe('neutral');
  });
});
