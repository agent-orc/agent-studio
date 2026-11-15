import { describe, expect, it } from 'vitest';
import { postProcessingActivityTone } from './post-processing-activity.component';

describe('postProcessingActivityTone', () => {
  it('maps every activity state to a status tone and falls back to neutral', () => {
    expect(['active', 'gate-queued', 'waiting', 'later-state'].map(postProcessingActivityTone))
      .toEqual(['running', 'warning', 'neutral', 'neutral']);
  });
});
