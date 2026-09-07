import { describe, expect, it } from 'vitest';
import { projectScopeForTab, type StudioProjectScope } from './studio-shell.project-scope';
import type { StudioTab } from './studio-shell.types';

/**
 * Direct matrix over the app-wide project-scope policy.
 *
 * The contract in one line: `null` means "All projects", a string narrows the
 * app to that project, `undefined` means "leave the current scope alone".
 */
describe('projectScopeForTab', () => {
  const TASK_KEY = 'C:/watch::task-a';
  /** Job index holding exactly the one task above, in "Project A". */
  const knownTask = (taskKey: string) => taskKey === TASK_KEY ? 'Project A' : undefined;
  /** Job index that has not loaded yet - every lookup misses. */
  const noJobsYet = () => undefined;

  const cases: readonly {
    name: string;
    tab: StudioTab | null;
    resolve?: (taskKey: string) => string | undefined;
    expected: StudioProjectScope;
  }[] = [
    { name: 'no active tab leaves the scope untouched', tab: null, expected: undefined },
    {
      name: 'the cross-project board is workspace-wide',
      tab: { kind: 'board', projectName: '__all__' },
      expected: null,
    },
    {
      name: 'a project board narrows to its project',
      tab: { kind: 'board', projectName: 'Project A' },
      expected: 'Project A',
    },
    { name: 'the feed is workspace-wide', tab: { kind: 'feed' }, expected: null },
    { name: 'chat history is workspace-wide', tab: { kind: 'chat-history' }, expected: null },
    {
      name: 'a Deck tab narrows to its project',
      tab: { kind: 'hub', projectName: 'Project A' },
      expected: 'Project A',
    },
    {
      name: 'a workspace-wide Epics tab is workspace-wide',
      tab: { kind: 'epics', projectName: null },
      expected: null,
    },
    {
      name: 'a project Epics tab narrows to its project',
      tab: { kind: 'epics', projectName: 'Project A' },
      expected: 'Project A',
    },
    {
      name: 'a Dossier tab narrows to its project',
      tab: { kind: 'workbench', projectName: 'Project A', workbenchId: 'w1' },
      expected: 'Project A',
    },
    // --- the AGT-2692 split -------------------------------------------------
    {
      name: 'a task opened from a project surface narrows to the task project',
      tab: { kind: 'task', taskKey: TASK_KEY },
      expected: 'Project A',
    },
    {
      name: 'a task opened from the All-projects board stays workspace-wide',
      tab: { kind: 'task', taskKey: TASK_KEY, originScope: 'all-projects' },
      expected: null,
    },
    {
      name: 'an All-projects task never consults the job index for its scope',
      tab: { kind: 'task', taskKey: TASK_KEY, originScope: 'all-projects' },
      resolve: () => {
        throw new Error('the job index must not drive the app-wide scope here');
      },
      expected: null,
    },
    {
      name: 'a project-scoped task whose job has not loaded leaves the scope untouched',
      tab: { kind: 'task', taskKey: TASK_KEY },
      resolve: noJobsYet,
      expected: undefined,
    },
    {
      name: 'an activity tab follows its task project',
      tab: { kind: 'activity', taskKey: TASK_KEY },
      expected: 'Project A',
    },
    {
      name: 'a diff tab leaves the scope untouched',
      tab: { kind: 'diff', commitSha: 'abc123' },
      expected: undefined,
    },
    {
      name: 'the welcome tab leaves the scope untouched',
      tab: { kind: 'welcome' },
      expected: undefined,
    },
  ];

  for (const c of cases) {
    it(c.name, () => {
      expect(projectScopeForTab(c.tab, c.resolve ?? knownTask)).toBe(c.expected);
    });
  }
});
