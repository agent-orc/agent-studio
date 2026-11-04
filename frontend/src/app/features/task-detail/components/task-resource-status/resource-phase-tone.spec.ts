import { describe, expect, it } from 'vitest';
import { resourcePhaseTone } from './resource-phase-tone';

describe('resourcePhaseTone', () => {
  it.each([
    ['idle', 'neutral'],
    ['loading', 'running'],
    ['warming', 'running'],
    ['ready', 'success'],
    ['stale', 'warning'],
    ['unavailable', 'warning'],
    ['error', 'danger'],
    ['future-phase', 'neutral'],
    [null, 'neutral'],
  ])('maps %s to %s', (phase, tone) => {
    expect(resourcePhaseTone(phase)).toBe(tone);
  });
});
