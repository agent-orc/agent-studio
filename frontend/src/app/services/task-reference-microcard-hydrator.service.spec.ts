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

  // AGT-2793, operator report: a worktree path rendering a task chip mid-path.
  // Worktree directories are named after their task (`.../worktrees/AGT-2814/
  // frontend/...`), so the key sits between two slashes - a path segment, not
  // a reference - and must not linkify.
  it('rejects a key embedded in a filesystem path', () => {
    expect(taskReferenceCandidates('/home/agent/runner-work/PROJ-002/worktrees/AGT-2814/frontend/src/app.ts')).toEqual([]);
    expect(taskReferenceCandidates('C:\\work\\worktrees\\AGT-2814\\frontend')).toEqual([]);
    expect(taskReferenceCandidates('runner-work/AGT-2814/frontend')).toEqual([]);
  });

  it('still finds a key that is merely adjacent to path-like text', () => {
    expect(taskReferenceCandidates('See AGT-2793 for the frontend/src fix')).toEqual([
      { start: 4, end: 12, key: 'AGT-2793' },
    ]);
  });
});
