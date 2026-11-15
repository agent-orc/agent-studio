import { describe, expect, it } from 'vitest';
import { interimTone, rateLimitTone, sessionChipTone, watchdogTone } from './protocol-pane-status-tones';

describe('protocol pane status tones', () => {
  it('maps session continuity', () => {
    expect(sessionChipTone('continued')).toBe('success');
    expect(sessionChipTone('fresh')).toBe('info');
    expect(sessionChipTone('lost')).toBe('warning');
    expect(sessionChipTone('other')).toBe('neutral');
  });

  it('maps watchdog states', () => {
    expect(watchdogTone('healthy')).toBe('running');
    expect(watchdogTone('quiet')).toBe('warning');
    expect(watchdogTone('suspicious')).toBe('warning');
    expect(watchdogTone('hung')).toBe('danger');
    expect(watchdogTone('idle')).toBe('neutral');
    expect(watchdogTone('later')).toBe('neutral');
  });

  it('maps rate-limit window status', () => {
    expect(rateLimitTone('allowed')).toBe('success');
    expect(rateLimitTone('exceeded')).toBe('warning');
    expect(rateLimitTone(null)).toBe('neutral');
  });

  it('maps interim status', () => {
    expect(interimTone('pending')).toBe('running');
    expect(interimTone('ready')).toBe('info');
    expect(interimTone('failed')).toBe('danger');
    expect(interimTone('idle')).toBe('neutral');
  });
});
