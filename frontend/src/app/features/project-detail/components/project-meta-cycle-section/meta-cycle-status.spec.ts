import { describe, expect, it } from 'vitest';
import type { MetaCycleVerdict } from '../../../../models/supervisor.model';
import { metaCycleHistoryTone, metaCycleVerdictTone } from './meta-cycle-status';

describe('meta-cycle status tones', () => {
  it('maps the current verdict', () => {
    expect(metaCycleVerdictTone('healthy')).toBe('success');
    expect(metaCycleVerdictTone('fixTriggering')).toBe('warning');
    expect(metaCycleVerdictTone('escalationOnly')).toBe('danger');
    expect(metaCycleVerdictTone('aborted')).toBe('neutral');
    expect(metaCycleVerdictTone(null)).toBe('neutral');
    expect(metaCycleVerdictTone('later' as MetaCycleVerdict)).toBe('neutral');
  });

  it('never keeps an acute tone for a past cycle', () => {
    expect(metaCycleHistoryTone('escalationOnly')).toBe('warning');
    expect(metaCycleHistoryTone('healthy')).toBe('success');
  });
});
