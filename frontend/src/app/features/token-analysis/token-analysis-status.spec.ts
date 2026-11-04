import { describe, expect, it } from 'vitest';
import { cardOutcomeTone, stepResultTone } from './token-analysis-status';

describe('token analysis status tones', () => {
  it('maps every card outcome, unknown outcomes stay neutral', () => {
    expect(cardOutcomeTone('integrated')).toBe('success');
    expect(cardOutcomeTone('completed')).toBe('success');
    expect(cardOutcomeTone('delivered')).toBe('success');
    expect(cardOutcomeTone('escalated')).toBe('warning');
    expect(cardOutcomeTone('blocked')).toBe('warning');
    expect(cardOutcomeTone('in-progress')).toBe('running');
    expect(cardOutcomeTone('open')).toBe('neutral');
    expect(cardOutcomeTone('archived')).toBe('neutral');
    expect(cardOutcomeTone(null)).toBe('neutral');
  });

  it('maps pass-like and fail-like step results, free text stays neutral', () => {
    expect(stepResultTone('passed')).toBe('success');
    expect(stepResultTone('OK')).toBe('success');
    expect(stepResultTone('fail')).toBe('warning');
    expect(stepResultTone('needs changes')).toBe('neutral');
    expect(stepResultTone(null)).toBe('neutral');
  });
});
