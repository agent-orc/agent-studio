import { describe, expect, it } from 'vitest';
import { healthStateTone, historyRunStateTone, runStateTone } from './deploy-status-tone';

describe('deploy status tones', () => {
  it('maps every run and step state onto a tone', () => {
    const matrix: Record<string, string> = {
      succeeded: 'success', failed: 'danger', running: 'running', queued: 'info',
      'awaiting-approval': 'info', 'waiting-for-intervention': 'info', cancelled: 'neutral',
      unknown: 'neutral', pending: 'neutral', skipped: 'neutral',
    };
    for (const [state, tone] of Object.entries(matrix)) expect(runStateTone(state)).toBe(tone);
  });

  it('falls back to neutral for a state this build does not know', () => {
    expect(runStateTone('paused-by-moon')).toBe('neutral');
    expect(runStateTone(null)).toBe('neutral');
    expect(runStateTone('toString')).toBe('neutral');
    expect(healthStateTone('later')).toBe('neutral');
  });

  it('maps every health state onto a tone', () => {
    const matrix: Record<string, string> = {
      healthy: 'success', up: 'success', degraded: 'warning', unhealthy: 'danger',
      down: 'danger', invalid: 'danger', unknown: 'neutral',
    };
    for (const [state, tone] of Object.entries(matrix)) expect(healthStateTone(state)).toBe(tone);
  });

  it('keeps a past failure calm in run history (R4)', () => {
    expect(historyRunStateTone('failed')).toBe('warning');
    expect(historyRunStateTone('succeeded')).toBe('success');
    expect(historyRunStateTone('running')).toBe('running');
    expect(historyRunStateTone('later')).toBe('neutral');
  });
});
