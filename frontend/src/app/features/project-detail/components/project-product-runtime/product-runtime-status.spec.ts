import { describe, expect, it } from 'vitest';
import { runtimeLevelTone, runtimeStatusTone } from './product-runtime-status';

describe('product runtime status tones', () => {
  it('maps every log level and falls back to neutral', () => {
    expect(['Trace', 'Debug', 'Info', 'Warn', 'Error', 'Fatal', 'Other', null].map(runtimeLevelTone))
      .toEqual(['neutral', 'neutral', 'neutral', 'warning', 'danger', 'danger', 'neutral', 'neutral']);
  });

  it('maps every event outcome and falls back to neutral', () => {
    expect(['Ok', 'Failed', 'Timeout', 'Cancelled', 'Skipped', 'toString', undefined].map(runtimeStatusTone))
      .toEqual(['success', 'danger', 'danger', 'neutral', 'neutral', 'neutral', 'neutral']);
  });
});
