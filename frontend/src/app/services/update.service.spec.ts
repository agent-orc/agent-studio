import { describe, expect, it } from 'vitest';
import { resolveUpdateServiceUrl } from './update.service';

describe('resolveUpdateServiceUrl', () => {
  it('skips machine-local polling on an HTTPS website without a configured service', () => {
    expect(resolveUpdateServiceUrl('https://studio.example/board')).toBeNull();
  });

  it('keeps the local Update Service for an HTTP installation', () => {
    expect(resolveUpdateServiceUrl('http://localhost:4010/board')).toBe('http://localhost:5039');
  });

  it('uses an explicit service endpoint on HTTPS', () => {
    expect(resolveUpdateServiceUrl('https://studio.example/', 'https://updates.example'))
      .toBe('https://updates.example');
  });
});
