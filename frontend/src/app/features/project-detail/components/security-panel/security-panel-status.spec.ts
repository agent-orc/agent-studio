import { describe, expect, it } from 'vitest';
import { baselineTone, historyVerdictTone, verdictTone } from './security-panel-status';

describe('security panel status tones', () => {
  it('maps baseline badges', () => {
    expect(baselineTone('ok')).toBe('success');
    expect(baselineTone('stale')).toBe('warning');
    expect(baselineTone('failing')).toBe('danger');
    expect(baselineTone('missing')).toBe('neutral');
    expect(baselineTone('unknown')).toBe('neutral');
    expect(baselineTone('new')).toBe('neutral');
  });

  it('maps free-text verdicts', () => {
    expect(verdictTone('PASS')).toBe('success');
    expect(verdictTone('ok with notes')).toBe('success');
    expect(verdictTone('stale')).toBe('warning');
    expect(verdictTone('warn')).toBe('warning');
    expect(verdictTone('FAIL')).toBe('danger');
    expect(verdictTone('critical')).toBe('danger');
    expect(verdictTone('mixed')).toBe('neutral');
    expect(verdictTone(null)).toBe('neutral');
  });

  it('keeps past failures in the history list non-acute (R4)', () => {
    expect(historyVerdictTone('FAIL')).toBe('warning');
    expect(historyVerdictTone('pass')).toBe('success');
    expect(historyVerdictTone(undefined)).toBe('neutral');
  });
});
