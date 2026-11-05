import { describe, expect, it } from 'vitest';
import { QUALITY_STATE_TONES } from './quality-panel.component';

describe('QUALITY_STATE_TONES', () => {
  it('maps the review verdicts onto status tones', () => {
    expect(QUALITY_STATE_TONES.pass).toBe('success');
    expect(QUALITY_STATE_TONES.concerns).toBe('warning');
    expect(QUALITY_STATE_TONES.block).toBe('danger');
    expect(QUALITY_STATE_TONES.reviewing).toBe('running');
    expect(QUALITY_STATE_TONES['not-reviewed']).toBe('neutral');
  });
});
