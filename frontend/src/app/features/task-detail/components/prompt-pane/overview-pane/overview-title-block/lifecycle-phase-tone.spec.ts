import { describe, expect, it } from 'vitest';
import { lifecyclePhaseTone } from './lifecycle-phase-tone';

describe('lifecyclePhaseTone', () => {
  it.each([
    ['human-ready', 'neutral'],
    ['intake-running', 'running'],
    ['intake-blocked', 'danger'],
    ['intake-passed', 'success'],
    ['execution-running', 'running'],
    ['execution-stalled', 'warning'],
    ['quota-waiting', 'warning'],
    ['loop-waiting', 'info'],
    ['steer-pending', 'warning'],
    ['post-processing-running', 'running'],
    ['post-processing-blocked', 'danger'],
    ['awaiting-review', 'info'],
    ['integrating', 'running'],
    ['some-new-phase', 'neutral'],
    [null, 'neutral'],
  ])('maps %s to %s', (phase, tone) => {
    expect(lifecyclePhaseTone(phase)).toBe(tone);
  });
});
