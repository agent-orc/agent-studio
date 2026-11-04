import { describe, expect, it } from 'vitest';
import { steeringReceiptTone } from './steering-receipt-tone';

describe('steeringReceiptTone', () => {
  it.each([
    ['admitted', 'info'],
    ['requested', 'info'],
    ['running', 'running'],
    ['consumed', 'success'],
    ['recovered', 'warning'],
    ['unresolved', 'warning'],
    ['stopped', 'neutral'],
    ['quarantined', 'danger'],
    ['rejected', 'danger'],
    ['Consumed', 'success'],
    ['later-state', 'neutral'],
    [null, 'neutral'],
  ])('maps %s to %s', (state, tone) => {
    expect(steeringReceiptTone(state)).toBe(tone);
  });
});
