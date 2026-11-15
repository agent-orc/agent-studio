import { describe, expect, it } from 'vitest';
import { driftFindingStatusTone, driftScoreBandTone, driftSeverityTone } from './drift-status-tones';

describe('drift status tones', () => {
  it('maps every severity, case-insensitively, and falls back to neutral', () => {
    expect(['Info', 'Warn', 'High', 'Critical', 'critical', 'Bogus', null].map(driftSeverityTone))
      .toEqual(['neutral', 'warning', 'warning', 'danger', 'danger', 'neutral', 'neutral']);
  });

  it('maps every tracking status and falls back to neutral', () => {
    expect(['New', 'Tracked', 'Accepted', 'Ignored', 'Resolved', 'later', undefined].map(driftFindingStatusTone))
      .toEqual(['neutral', 'info', 'success', 'neutral', 'success', 'neutral', 'neutral']);
  });

  it('maps every score band and falls back to neutral', () => {
    expect(['Healthy', 'Watch', 'Warn', 'Critical', 'Unknown', 'other'].map(driftScoreBandTone))
      .toEqual(['success', 'info', 'warning', 'danger', 'neutral', 'neutral']);
  });
});
