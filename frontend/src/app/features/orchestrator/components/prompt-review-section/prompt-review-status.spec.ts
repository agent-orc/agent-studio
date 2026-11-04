import { describe, expect, it } from 'vitest';
import { promptFindingTone, promptReviewStatus } from './prompt-review-status';

describe('prompt review status mapping', () => {
  it('maps every review status to a label and tone, unknown and missing to neutral', () => {
    expect(['current', 'needs-review', 'stale', null, 'later'].map(promptReviewStatus)).toEqual([
      { label: 'current', tone: 'success' },
      { label: 'needs review', tone: 'warning' },
      { label: 'stale', tone: 'danger' },
      { label: 'not reviewed', tone: 'neutral' },
      { label: 'later', tone: 'neutral' },
    ]);
  });

  it('maps finding severities and falls back to neutral', () => {
    expect(['error', 'warning', 'info', 'other'].map(promptFindingTone))
      .toEqual(['danger', 'warning', 'info', 'neutral']);
  });
});
