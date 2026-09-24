import { describe, expect, it } from 'vitest';
import { parseRuntimeSentinels } from './runtime-sentinel.parser';

describe('parseRuntimeSentinels', () => {
  it('projects the AGT-2794 marker and keeps the first duplicate summary', () => {
    const parsed = parseRuntimeSentinels(
      'Result\n[[ASPECT_VERDICT: status=concerns; summary=Clean diff; evidence_checked=a.ts, b.spec.ts; missing=none; summary=one leftover issue.]] [[TASK_DONE]]',
    );

    expect(parsed.text).toBe('Result');
    expect(parsed.aspects[0]).toMatchObject({
      status: 'concerns',
      summary: 'Clean diff',
      evidenceChecked: ['a.ts', 'b.spec.ts'],
      missing: 'none',
      detail: ['summary=one leftover issue.'],
      malformed: ['duplicate-key'],
    });
    expect(parsed.terminals).toEqual([{ kind: 'done', detail: '' }]);
  });

  it('leaves unknown bracketed markers as text', () => {
    expect(parseRuntimeSentinels('Keep [[FUTURE_MARKER: value=1]]').text)
      .toBe('Keep [[FUTURE_MARKER: value=1]]');
  });
});
