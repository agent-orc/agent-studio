import { describe, expect, it } from 'vitest';
import { urlDiagnosisTone, urlPillTone, urlStripTone } from './project-urls-status';

describe('project URL status tones', () => {
  it('maps every per-URL state and falls back to neutral', () => {
    expect(urlPillTone('running')).toBe('success');
    expect(urlPillTone('building')).toBe('running');
    expect(urlPillTone('failed')).toBe('danger');
    expect(urlPillTone('blocked')).toBe('warning');
    expect(urlPillTone('offline')).toBe('neutral');
    expect(urlPillTone('local-only')).toBe('neutral');
    expect(urlPillTone('unknown')).toBe('neutral');
    expect(urlPillTone('something-new')).toBe('neutral');
  });

  it('maps the overall strip', () => {
    expect(urlStripTone(2, 2, false)).toBe('success');
    expect(urlStripTone(2, 2, true)).toBe('running');
    expect(urlStripTone(1, 2, false)).toBe('neutral');
    expect(urlStripTone(0, 0, false)).toBe('neutral');
  });

  it('maps test-setup diagnoses', () => {
    expect(urlDiagnosisTone('running')).toBe('success');
    expect(urlDiagnosisTone('starting')).toBe('running');
    expect(urlDiagnosisTone('not-started')).toBe('neutral');
    for (const c of ['command-unavailable', 'invalid-cwd', 'process-exited', 'http-error-response']) {
      expect(urlDiagnosisTone(c)).toBe('danger');
    }
    for (const c of ['port-in-use', 'port-never-opened', 'timeout', 'content-not-renderable', 'invalid-configuration']) {
      expect(urlDiagnosisTone(c)).toBe('warning');
    }
    expect(urlDiagnosisTone('future-class')).toBe('neutral');
  });
});
