import { describe, expect, it } from 'vitest';
import type { CliModelInfo } from './cli.model';
import { orderModelCatalog } from './model-catalog-ordering';

function model(id: string, overrides: Partial<CliModelInfo> = {}): CliModelInfo {
  return {
    id,
    label: id,
    multiplier: null,
    vendor: null,
    isDefault: false,
    available: true,
    deprecated: false,
    ...overrides,
  };
}

describe('orderModelCatalog', () => {
  it('sorts leading family generations first and older generations newest-first', () => {
    const ordered = orderModelCatalog([
      model('claude-opus-4-7'),
      model('claude-haiku-4-5'),
      model('claude-sonnet-4-6'),
      model('claude-opus-5'),
      model('claude-fable-5-1'),
      model('claude-opus-4-8'),
      model('claude-sonnet-5'),
    ]);

    expect(ordered.map((item) => item.id)).toEqual([
      'claude-fable-5-1',
      'claude-opus-5',
      'claude-sonnet-5',
      'claude-opus-4-8',
      'claude-opus-4-7',
      'claude-sonnet-4-6',
      'claude-haiku-4-5',
    ]);
    expect(ordered.slice(0, 3).every((item) => !item.deprecated)).toBe(true);
    expect(ordered.slice(3).every((item) => item.olderGeneration && !item.deprecated)).toBe(true);
  });

  it('marks superseded generations older without deprecating them', () => {
    const ordered = orderModelCatalog([
      model('claude-opus-4-7'),
      model('claude-opus-5'),
      model('claude-opus-4-8'),
    ]);

    expect(ordered[0]).toMatchObject({ id: 'claude-opus-5', deprecated: false });
    expect(ordered[1]).toMatchObject({
      id: 'claude-opus-4-8',
      deprecated: false,
      olderGeneration: true,
      availabilityNote: 'Older generation',
    });
    expect(ordered[2]).toMatchObject({
      id: 'claude-opus-4-7',
      deprecated: false,
      olderGeneration: true,
      availabilityNote: 'Older generation',
    });
  });

  it('preserves explicit catalog notes and keeps every available model selectable', () => {
    const input = [
      model('gpt-5.5', { deprecated: true, availabilityNote: 'Superseded by GPT-5.6.' }),
      model('gpt-5.6-sol'),
      model('gpt-5.4-mini'),
    ];

    const ordered = orderModelCatalog(input);

    expect(ordered).toHaveLength(input.length);
    expect(ordered.every((item) => item.available !== false)).toBe(true);
    expect(ordered.find((item) => item.id === 'gpt-5.5')).toMatchObject({
      deprecated: true,
      availabilityNote: 'Superseded by GPT-5.6.',
    });
  });

  it('places genuinely unavailable entries last without making older entries unavailable', () => {
    const ordered = orderModelCatalog([
      model('claude-opus-4-7'),
      model('claude-opus-5'),
      model('claude-opus-4-1', { available: false, deprecated: true }),
    ]);

    expect(ordered.map((item) => item.id)).toEqual([
      'claude-opus-5',
      'claude-opus-4-7',
      'claude-opus-4-1',
    ]);
    expect(ordered[1].available).toBe(true);
    expect(ordered[2].available).toBe(false);
  });

  it('classifies unavailable Claude 4.x entries as older without making them selectable', () => {
    const ordered = orderModelCatalog([
      model('claude-opus-5'),
      model('claude-sonnet-4-6', {
        available: false,
        availabilityNote: 'Known in registry but not reported by the installed Claude CLI.',
      }),
    ]);

    expect(ordered[1]).toMatchObject({
      id: 'claude-sonnet-4-6',
      available: false,
      deprecated: false,
      olderGeneration: true,
      availabilityNote: 'Known in registry but not reported by the installed Claude CLI.',
    });
  });

  it('keeps discovery order for ids without a conventional numeric generation', () => {
    const ordered = orderModelCatalog([
      model('claude-latest'),
      model('claude-stable'),
    ]);

    expect(ordered.map((item) => item.id)).toEqual(['claude-latest', 'claude-stable']);
  });
});
