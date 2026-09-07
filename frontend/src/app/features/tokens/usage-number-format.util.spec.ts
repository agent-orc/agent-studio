import { describe, expect, it } from 'vitest';
import { formatUsageCurrency, formatUsageTokens } from './usage-number-format.util';

describe('usage number formatting', () => {
  it('uses grouped, two-decimal USD consistently', () => {
    expect(formatUsageCurrency(59_996.74, { locale: 'en-US' })).toBe('$59,996.74');
    expect(formatUsageCurrency(0.004, { locale: 'en-US' })).toBe('$0.00');
  });

  it('uses one decimal and grouped compact token units', () => {
    expect(formatUsageTokens(0, { locale: 'en-US' })).toBe('0.0K');
    expect(formatUsageTokens(700, { locale: 'en-US' })).toBe('0.7K');
    expect(formatUsageTokens(67_000, { locale: 'en-US' })).toBe('67.0K');
    expect(formatUsageTokens(10_940_000_000, { locale: 'en-US' })).toBe('10,940.0M');
    expect(formatUsageTokens(10_940_000_000, { locale: 'en-US', unit: 'B' })).toBe('10.9B');
  });

  it('honours the viewer locale for separators', () => {
    expect(formatUsageTokens(10_940_000_000, { locale: 'de-DE' })).toBe('10.940,0M');
    expect(formatUsageCurrency(59_996.74, { locale: 'de-DE' })).toMatch(/^59\.996,74\s\$/u);
  });
});
