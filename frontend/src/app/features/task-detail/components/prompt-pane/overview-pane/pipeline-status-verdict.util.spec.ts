import { describe, expect, it } from 'vitest';
import { distinctStepVerdict, reviewRoundTone } from './pipeline-status-verdict.util';

describe('distinctStepVerdict', () => {
  it.each([
    ['failed', 'ERROR'],
    ['failed', 'fail'],
    ['passed', 'PASS'],
    ['passed', 'succeeded'],
    ['skipped', 'skipped'],
    ['disabled', 'inactive'],
  ])('drops %s/%s because the status icon already carries it', (status, verdict) => {
    expect(distinctStepVerdict(status, verdict)).toBeNull();
  });

  it.each([
    ['failed', 'needsinput'],
    ['passed', 'concerns'],
    ['passed', 'attempt 2'],
    ['failed', 'loop-detected'],
  ])('keeps %s/%s because it adds routing or result detail', (status, verdict) => {
    expect(distinctStepVerdict(status, verdict)).toBe(verdict);
  });
});

describe('reviewRoundTone', () => {
  it('tones carried-over, fixed and still-open findings', () => {
    expect(reviewRoundTone({ carriedOverFrom: 'r1' } as never)).toBe('info');
    expect(reviewRoundTone({ fixedInRun: 2 } as never)).toBe('success');
    expect(reviewRoundTone({ stillOpen: true } as never)).toBe('warning');
    expect(reviewRoundTone(null)).toBe('neutral');
  });
});
