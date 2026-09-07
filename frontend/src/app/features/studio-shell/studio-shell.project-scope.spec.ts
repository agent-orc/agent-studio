import { describe, expect, it } from 'vitest';
import type { StudioTab } from './studio-shell.types';
import { tabProjectScope } from './studio-shell.project-scope';

/**
 * Matrix over the scope policy (AGT-2692). The contract worth pinning is the
 * three-valued answer: a project name narrows the workspace, `null` keeps it
 * cross-project, and `undefined` means "this tab claims no scope" so the
 * caller keeps whatever was already active.
 */
describe('tabProjectScope', () => {
  const cases: ReadonlyArray<[string, StudioTab | null, string | null | undefined]> = [
    ['no active tab', null, undefined],
    ['All-projects board', { kind: 'board', projectName: '__all__' }, null],
    ['project board', { kind: 'board', projectName: 'Project A' }, 'Project A'],
    ['feed', { kind: 'feed' }, null],
    ['chat history', { kind: 'chat-history' }, null],
    ['workspace-wide epics', { kind: 'epics', projectName: null }, null],
    ['project epics', { kind: 'epics', projectName: 'Project A' }, 'Project A'],
    ['workspace-wide workbenches', { kind: 'workbenches', projectName: null }, null],
    ['project deck', { kind: 'hub', projectName: 'Project A' }, 'Project A'],
    ['workbench', { kind: 'workbench', projectName: 'Project A', workbenchId: 'w1' }, 'Project A'],
    ['diff', { kind: 'diff', commitSha: 'abc123' }, undefined],
    ['welcome', { kind: 'welcome' }, undefined],
  ];

  for (const [label, tab, expected] of cases) {
    it(`${label} → ${String(expected)}`, () => {
      expect(tabProjectScope(tab)).toBe(expected);
    });
  }

  describe('task and activity tabs answer with the scope they were opened from', () => {
    it('keeps the workspace cross-project for a task opened from All projects', () => {
      expect(tabProjectScope({ kind: 'task', taskKey: 'C:/w::a', originScope: null })).toBeNull();
      expect(tabProjectScope({ kind: 'activity', taskKey: 'C:/w::a', originScope: null })).toBeNull();
    });

    it('narrows to the project a task was opened from', () => {
      expect(tabProjectScope({ kind: 'task', taskKey: 'C:/w::a', originScope: 'Project A' }))
        .toBe('Project A');
    });

    it('claims no scope when the origin was never recorded (cold deep link)', () => {
      // The task's own project is a *data* handle for the detail view, not a
      // workspace scope, so the policy itself never reaches for it. Resolving
      // that fallback is the caller's job.
      expect(tabProjectScope({ kind: 'task', taskKey: 'C:/w::a' })).toBeUndefined();
      expect(tabProjectScope({ kind: 'activity', taskKey: 'C:/w::a' })).toBeUndefined();
    });
  });
});
