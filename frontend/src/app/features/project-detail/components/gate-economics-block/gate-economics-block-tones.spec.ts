import { expect, it } from 'vitest';
import { gateStateTone, nightlyGuardTone } from './gate-economics-block';

// Pure mapping test, kept apart from the component spec whose afterEach
// verifies the HTTP testing controller.
it('maps develop and nightly guard states onto tones', () => {
  expect(gateStateTone('green')).toBe('success');
  expect(gateStateTone('running')).toBe('running');
  expect(gateStateTone('red')).toBe('danger');
  expect(gateStateTone(null)).toBe('neutral');
  expect(nightlyGuardTone('red · abcdef123456')).toBe('danger');
  expect(nightlyGuardTone('skipped · abcdef123456')).toBe('neutral');
  expect(nightlyGuardTone(null)).toBe('neutral');
});
