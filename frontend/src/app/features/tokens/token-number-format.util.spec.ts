import { describe, expect, it } from 'vitest';
import { formatCompactTokens, formatCompactUsd } from './token-number-format.util';

describe('formatCompactUsd', () => {
  it('adds thousands separators above $1,000', () => {
    expect(formatCompactUsd(59_996.74)).toBe('$59,996.74');
  });

  it('uses two decimals at/above $1', () => {
    expect(formatCompactUsd(1.5)).toBe('$1.50');
  });

  it('uses three decimals between $0.10 and $1', () => {
    expect(formatCompactUsd(0.125)).toBe('$0.125');
  });

  it('uses four decimals below $0.10 so a tiny real cost never reads as zero', () => {
    expect(formatCompactUsd(0.0034)).toBe('$0.0034');
  });

  it('renders exactly zero as $0.00', () => {
    expect(formatCompactUsd(0)).toBe('$0.00');
  });

  it('renders a non-finite value as $0.00', () => {
    expect(formatCompactUsd(Number.NaN)).toBe('$0.00');
  });
});

describe('formatCompactTokens', () => {
  it('adds thousands separators to a multi-million M value', () => {
    expect(formatCompactTokens(10_940_000_000)).toBe('10,940.0M');
  });

  it('renders sub-1000 counts as plain integers', () => {
    expect(formatCompactTokens(742)).toBe('742');
  });

  it('renders thousands with a K suffix', () => {
    expect(formatCompactTokens(12_345)).toBe('12K');
    expect(formatCompactTokens(1_234)).toBe('1.2K');
  });

  it('keeps the M suffix (with grouping) above one billion instead of switching to B', () => {
    expect(formatCompactTokens(2_500_000_000)).toBe('2,500.0M');
  });

  it('renders a non-finite value as 0', () => {
    expect(formatCompactTokens(Number.NaN)).toBe('0');
  });
});
