import { describe, expect, it } from 'vitest';
import {
  formatTokenCostTotal,
  formatTokenCount,
  formatTokenCurrencyUsd,
  formatTokenExactCount,
} from './token-number-format.util';

describe('token number formatting', () => {
  it('formats USD with locale grouping and exactly two decimals', () => {
    expect(formatTokenCurrencyUsd(59_996.74, 'en-US')).toBe('$59,996.74');
    expect(formatTokenCurrencyUsd(0.004, 'en-US')).toBe('$0.00');
  });

  it('formats compact tokens with one decimal and auto-scales through billions', () => {
    expect(formatTokenCount(67_000, { locale: 'en-US' })).toBe('67.0K');
    expect(formatTokenCount(10_940_000, { locale: 'en-US' })).toBe('10.9M');
    expect(formatTokenCount(10_940_000_000, { locale: 'en-US' })).toBe('10.9B');
  });

  it('formats exact token counters with the viewer locale', () => {
    expect(formatTokenExactCount(10_940_000, 'en-US')).toBe('10,940,000');
    expect(formatTokenExactCount(10_940_000, 'de-DE')).toBe('10.940.000');
  });

  it('can cap the largest unit while retaining locale grouping', () => {
    expect(formatTokenCount(10_940_000_000, {
      locale: 'en-US',
      maximumUnit: 'M',
    })).toBe('10,940.0M');
    expect(formatTokenCount(10_940_000_000, {
      locale: 'de-DE',
      maximumUnit: 'M',
    })).toBe('10.940,0M');
  });

  it('marks a known subtotal when some rows are unpriced', () => {
    expect(formatTokenCostTotal(59_996.74, 1, 'en-US'))
      .toBe('$59,996.74 + 1 unpriced');
  });
});
