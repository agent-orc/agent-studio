import { describe, expect, it } from 'vitest';
import { formatZonedTime, formatZonedWithUtc, usageTimeZone } from './usage-time.util';

describe('usage time format (AGT-3794-F14)', () => {
  const at = '2026-11-15T21:34:00Z';

  it('always names the zone', () => {
    expect(formatZonedTime(at, 'Europe/Berlin')).toBe('Sun 15 Nov, 22:34 Europe/Berlin');
    expect(formatZonedTime(at, null)).toBe('Sun 15 Nov, 21:34 UTC');
  });

  it('adds the UTC equivalent only for a non-UTC zone', () => {
    expect(formatZonedWithUtc(at, 'Europe/Berlin')).toBe('Sun 15 Nov, 22:34 Europe/Berlin (Sun 15 Nov, 21:34 UTC)');
    expect(formatZonedWithUtc(at, 'UTC')).toBe('Sun 15 Nov, 21:34 UTC');
  });

  it('reads UTC aliases and invalid zones as UTC', () => {
    expect(usageTimeZone('Etc/UTC')).toBe('UTC');
    expect(usageTimeZone('GMT')).toBe('UTC');
    expect(usageTimeZone('Not/AZone')).toBe('UTC');
    expect(formatZonedWithUtc(at, 'Etc/UTC')).toBe('Sun 15 Nov, 21:34 UTC');
  });

  it('returns null for missing or invalid instants', () => {
    expect(formatZonedTime(null, 'UTC')).toBeNull();
    expect(formatZonedWithUtc('nonsense', 'UTC')).toBeNull();
  });
});
