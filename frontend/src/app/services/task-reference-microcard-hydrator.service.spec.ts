import { describe, expect, it } from 'vitest';
import { taskReferenceCandidates } from './task-reference-microcard-hydrator.service';

describe('taskReferenceCandidates', () => {
  it('finds compact keys with their exact word boundaries', () => {
    expect(taskReferenceCandidates('See AGT-2050, then CAR-2.')).toEqual([
      { start: 4, end: 12, key: 'AGT-2050' },
      { start: 19, end: 24, key: 'CAR-2' },
    ]);
  });

  it('rejects keys embedded in identifiers and malformed short codes', () => {
    expect(taskReferenceCandidates('AGT-2_y A-2 TOOLONG7-2 AGT-x')).toEqual([]);
  });

  it('does not turn a task key inside a path, branch, URL, or inline code into a microcard', () => {
    expect(taskReferenceCandidates('See AGT-2975; branch=agent-studio/quarantine/AGT-2975/run; /worktrees/AGT-2975; https://example.test/AGT-2975; `AGT-2975`')).toEqual([
      { start: 4, end: 12, key: 'AGT-2975' },
    ]);
  });
});
