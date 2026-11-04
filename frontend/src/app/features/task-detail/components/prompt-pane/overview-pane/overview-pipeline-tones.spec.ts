import { describe, expect, it } from 'vitest';
import { decisionBadgeStatusTone, pipelineGroupStatusTone, runOutcomeCounterTone } from './overview-pipeline-tones';

describe('overview pipeline tones', () => {
  it.each([
    ['ok', 'success'], ['danger', 'danger'], ['warn', 'running'], ['concern', 'warning'], ['muted', 'neutral'],
    ['not-applicable', 'neutral'], ['not-run', 'neutral'], ['neutral', 'neutral'], ['later', 'neutral'],
  ])('maps group tone %s to %s', (tone, expected) => {
    expect(pipelineGroupStatusTone(tone)).toBe(expected);
  });

  it('maps final-verdict routes and neutralizes superseded attempts', () => {
    expect(decisionBadgeStatusTone('ok', false)).toBe('success');
    expect(decisionBadgeStatusTone('warn', false)).toBe('warning');
    expect(decisionBadgeStatusTone('danger', false)).toBe('danger');
    expect(decisionBadgeStatusTone('neutral', false)).toBe('neutral');
    expect(decisionBadgeStatusTone('danger', true)).toBe('neutral');
    expect(decisionBadgeStatusTone(null, false)).toBe('neutral');
  });

  it('maps run outcome counters', () => {
    expect(runOutcomeCounterTone('pass')).toBe('success');
    expect(runOutcomeCounterTone('fail')).toBe('danger');
    expect(runOutcomeCounterTone('pending')).toBe('neutral');
  });
});
