import { describe, expect, it } from 'vitest';
import { pipelineStepStatusTone, postStepAttemptTone, stepVerdictTone } from './step-status-tone';

describe('pipeline step status tones', () => {
  it.each([
    ['passed', 'success'], ['failed', 'danger'], ['running', 'running'], ['needsDecision', 'warning'],
    ['skipped', 'neutral'], ['notApplicable', 'neutral'], ['not-run', 'neutral'], ['planned', 'neutral'],
    ['disabled', 'neutral'], ['pending', 'neutral'], ['later', 'neutral'], [null, 'neutral'],
  ])('maps step status %s to %s', (status, tone) => {
    expect(pipelineStepStatusTone(status)).toBe(tone);
  });

  it('treats an attention-required skip as danger', () => {
    expect(pipelineStepStatusTone('skipped', true)).toBe('danger');
    expect(pipelineStepStatusTone('passed', true)).toBe('success');
  });

  it.each([
    ['ok', 'success'], ['OK', 'success'], ['warn', 'warning'], ['failed', 'danger'], ['timeout', 'neutral'],
  ])('maps post-step attempt %s to %s', (status, tone) => {
    expect(postStepAttemptTone(status)).toBe(tone);
  });

  it.each([
    ['pass', 'success'], ['success', 'success'], ['clean', 'success'], ['intended', 'success'],
    ['concerns', 'warning'], ['concern', 'warning'], ['at-risk', 'warning'], ['looping', 'warning'],
    ['block', 'danger'], ['blocked', 'danger'], ['drift', 'danger'], ['loop-detected', 'danger'],
    ['other', 'neutral'], [null, 'neutral'],
  ])('maps step verdict %s to %s', (verdict, tone) => {
    expect(stepVerdictTone(verdict)).toBe(tone);
  });
});
