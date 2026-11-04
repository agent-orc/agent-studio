import { describe, expect, it } from 'vitest';
import { batchAdvisorVerdictTone, batchDecisionTone, batchMemberStatusTone, batchOutcomeTone } from './batch-status-tones';

describe('batch status tones', () => {
  it('maps member status, falling back to neutral', () => {
    expect(['pending', 'in-batch', 'published', 'fallback', 'pre-check-red', 'later'].map(batchMemberStatusTone))
      .toEqual(['neutral', 'running', 'success', 'warning', 'danger', 'neutral']);
  });

  it('maps plan decisions', () => {
    expect(['wait', 'close', 'per-task', 'idle', 'disabled', 'other'].map(batchDecisionTone))
      .toEqual(['info', 'running', 'warning', 'neutral', 'neutral', 'neutral']);
  });

  it('maps run and replay outcomes case-insensitively', () => {
    expect(['Passed', 'failed', 'running', 'not replayed', 'applied', null, 'odd'].map(batchOutcomeTone))
      .toEqual(['success', 'danger', 'running', 'neutral', 'success', 'neutral', 'neutral']);
  });

  it('maps advisor verdicts', () => {
    expect(['approve', 'veto', 'abstain'].map(batchAdvisorVerdictTone)).toEqual(['success', 'danger', 'neutral']);
  });
});
