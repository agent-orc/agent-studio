import { describe, expect, it } from 'vitest';
import { mapThinkingLevel, mappedThinkingLevelNote } from './thinking-level-mapping';

describe('mapThinkingLevel (AGT-2903)', () => {
  const lunaLadder = ['low', 'medium', 'high', 'xhigh', 'max'];
  const xhighLadder = ['minimal', 'low', 'medium', 'high', 'xhigh'];

  it('keeps an offered level unchanged', () => {
    expect(mapThinkingLevel(lunaLadder, 'medium', 'high')).toEqual({ level: 'high', mappedFrom: null });
  });

  it('maps an unoffered known rung to the highest offered rung below it', () => {
    expect(mapThinkingLevel(lunaLadder, 'medium', 'ultra')).toEqual({ level: 'max', mappedFrom: 'ultra' });
    expect(mapThinkingLevel(xhighLadder, 'xhigh', 'ultra')).toEqual({ level: 'xhigh', mappedFrom: 'ultra' });
  });

  it('falls back to the model default for unknown or below-ladder levels', () => {
    expect(mapThinkingLevel(lunaLadder, 'medium', 'minimal')).toEqual({ level: 'medium', mappedFrom: 'minimal' });
    expect(mapThinkingLevel(lunaLadder, 'medium', 'turbo')).toEqual({ level: 'medium', mappedFrom: 'turbo' });
  });

  it('uses the default when nothing is pinned and returns nothing without a ladder', () => {
    expect(mapThinkingLevel(lunaLadder, 'medium', null)).toEqual({ level: 'medium', mappedFrom: null });
    expect(mapThinkingLevel([], 'medium', 'ultra')).toEqual({ level: null, mappedFrom: null });
  });

  it('describes a mapping for the operator', () => {
    expect(mappedThinkingLevelNote({ level: 'max', mappedFrom: 'ultra' }, 'gpt-6-luna'))
      .toBe('Pinned ultra is not offered by gpt-6-luna; runs at max.');
    expect(mappedThinkingLevelNote({ level: 'high', mappedFrom: null }, 'gpt-6-luna')).toBeNull();
  });
});
