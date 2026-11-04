import { describe, expect, it } from 'vitest';
import { attemptCleanupTone, attemptOccupancyDisplay, providerStatusTone, zonePolicyTone } from './environment-flow.model';

describe('environment flow status tones', () => {
  it('maps attempt occupancy and keeps unknown values neutral with their raw label', () => {
    expect(['occupied', 'released', 'orphan'].map(value => attemptOccupancyDisplay(value).tone))
      .toEqual(['running', 'neutral', 'warning']);
    expect(attemptOccupancyDisplay('parked')).toEqual({ tone: 'neutral', label: 'parked' });
  });

  it('flags only a failed cleanup', () => {
    expect(['failed', 'done', 'pending'].map(attemptCleanupTone)).toEqual(['warning', 'neutral', 'neutral']);
  });

  it('maps provider auth statuses and falls back to neutral', () => {
    expect(['valid', 'signed-out', 'rate-limited', 'unknown', 'toString', null].map(providerStatusTone))
      .toEqual(['success', 'danger', 'warning', 'neutral', 'neutral', 'neutral']);
  });

  it('maps zone policy states and falls back to neutral', () => {
    expect(['verified', 'stale', 'failed', 'not-reported', 'later'].map(zonePolicyTone))
      .toEqual(['success', 'warning', 'danger', 'neutral', 'neutral']);
  });
});
