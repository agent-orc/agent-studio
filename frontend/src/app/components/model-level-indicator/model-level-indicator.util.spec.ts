import { describe, expect, it } from 'vitest';
import { buildModelLevelPresentation } from './model-level-indicator.util';

describe('buildModelLevelPresentation', () => {
  it.each([
    ['gpt-5.6-sol', 'ultra', 'sol', 'SOL', 'u'],
    ['gpt-6-luna', 'medium', 'luna', 'LUN', 'm'],
    ['gpt-5.6-ter', 'xhigh', 'ter', 'TER', 'xh'],
    ['claude-opus-4-8', 'high', 'opus', 'OP4.8', 'h'],
    ['claude-sonnet-5', 'medium', 'sonnet', 'SON5', 'm'],
    ['claude-haiku-4-5', 'low', 'haiku', 'HAI4.5', 'l'],
    ['gemini-2.5-pro', 'high', 'gemini', 'GEM2.5P', 'h'],
    ['gpt-5.4-mini', 'minimal', 'openai', 'GPT5.4M', 'min'],
  ] as const)('maps %s to a stable family, model code, and level code', (model, level, family, code, levelCode) => {
    expect(buildModelLevelPresentation(model, level)).toEqual({ family, modelCode: code, levelCode });
  });

  it('keeps human and unknown fallbacks distinguishable', () => {
    expect(buildModelLevelPresentation(null, null, 'human')).toMatchObject({ family: 'human', modelCode: 'HUM' });
    expect(buildModelLevelPresentation(null, null, 'unknown')).toMatchObject({ family: 'unknown', modelCode: '?' });
  });
});
