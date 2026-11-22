import { describe, expect, it } from 'vitest';
import { TARGET_READINESS, deploymentHistoryTone, lastDeploymentTone } from './deployment-status-tone';

describe('deployment status tones', () => {
  it('keeps past failures calm in history lists (R4)', () => {
    const matrix: Record<string, string> = {
      healthy: 'success', ok: 'success', succeeded: 'success', 'rolled-back': 'warning', failed: 'warning',
      unknown: 'neutral', toString: 'neutral', later: 'neutral',
    };
    for (const [status, tone] of Object.entries(matrix)) expect(deploymentHistoryTone(status)).toBe(tone);
  });

  it('treats the latest deployment as current truth', () => {
    expect(lastDeploymentTone('ok')).toBe('success');
    expect(lastDeploymentTone('failed')).toBe('danger');
    expect(lastDeploymentTone('unknown')).toBe('neutral');
    expect(lastDeploymentTone(null)).toBe('neutral');
  });

  it('marks a target that still needs setup or the idle confirmation as warning', () => {
    expect(TARGET_READINESS.ready.tone).toBe('success');
    expect(TARGET_READINESS['setup-required'].tone).toBe('warning');
    expect(TARGET_READINESS['idle-confirmed'].tone).toBe('success');
    expect(TARGET_READINESS['idle-required'].tone).toBe('warning');
  });
});
