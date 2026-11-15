import { describe, expect, it } from 'vitest';
import { protocolSignalTone, protocolVerdictTone } from './protocol-verdict-tone';

describe('protocol verdict tones', () => {
  it.each([
    ['succeeded', 'Done', false, 'success'],
    ['failed', 'Execution failed', false, 'danger'],
    ['needs-decision', 'Blocked', false, 'warning'],
    ['unclear', 'Unclear', false, 'neutral'],
    ['unclear', 'Running', false, 'running'],
    ['unclear', 'No run yet', true, 'running'],
    ['later', 'Later', false, 'neutral'],
  ] as const)('maps %s / %s (running %s) to %s', (status, label, running, tone) => {
    expect(protocolVerdictTone({ status: status as never, label }, running)).toBe(tone);
  });

  it.each([
    ['succeeded', 'success'], ['failed', 'danger'], ['needs-decision', 'warning'], ['unclear', 'warning'], ['other', 'neutral'],
  ])('maps signal %s to %s', (status, tone) => {
    expect(protocolSignalTone(status)).toBe(tone);
  });
});
