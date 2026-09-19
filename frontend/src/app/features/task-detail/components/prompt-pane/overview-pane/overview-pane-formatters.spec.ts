import { describe, expect, it } from 'vitest';
import { formatTokens } from './overview-pane-formatters';

describe('Overview token formatting', () => {
  it.each([
    [999, '999'],
    [1_000, '1k'],
    [999_999, '1000k'],
    [1_000_000, '1.0M'],
    [2_000_000, '2.0M'],
  ])('formats %i tokens as %s', (tokens, expected) => {
    expect(formatTokens(tokens)).toBe(expected);
  });

  it('keeps up to two meaningful decimals for millions', () => {
    expect(formatTokens(41_650_000)).toBe('41.65M');
    expect(formatTokens(812_000)).toBe('812k');
  });
});
