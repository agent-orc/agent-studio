import { describe, expect, it } from 'vitest';
import type { StudioTab, TaskTab } from '../studio-shell.types';
import {
  ALL_PROJECTS_BOARD_NAME,
  normalizeTaskTabScope,
  resolveTaskTabScope,
  taskTabProjectScope,
} from './task-tab-scope';

/**
 * AGT-2692 — operator sighting (2026-08-29): opening a task from the
 * "All projects" board switched the whole workspace into that task's project.
 * The matrix below pins the separation that fixes it: the origin decides the
 * app scope, the task decides only its own data.
 */
describe('resolveTaskTabScope (origin → scope stamp)', () => {
  const cases: readonly [string, StudioTab, ReturnType<typeof resolveTaskTabScope>][] = [
    [
      'cross-project board stays workspace-wide',
      { kind: 'board', projectName: ALL_PROJECTS_BOARD_NAME },
      { kind: 'all-projects' },
    ],
    [
      'project board scopes to that project',
      { kind: 'board', projectName: 'Alpha' },
      { kind: 'project', projectName: 'Alpha' },
    ],
    ['orchestrator feed is workspace-wide', { kind: 'feed' }, { kind: 'all-projects' }],
    ['chat history is workspace-wide', { kind: 'chat-history' }, { kind: 'all-projects' }],
    ['workspace-wide epics overview', { kind: 'epics', projectName: null }, { kind: 'all-projects' }],
    [
      'project epics overview',
      { kind: 'epics', projectName: 'Alpha' },
      { kind: 'project', projectName: 'Alpha' },
    ],
    [
      'workspace-wide workbenches overview',
      { kind: 'workbenches', projectName: null },
      { kind: 'all-projects' },
    ],
    [
      'project Deck',
      { kind: 'hub', projectName: 'Alpha', section: 'overview' },
      { kind: 'project', projectName: 'Alpha' },
    ],
    [
      'project URL preview',
      { kind: 'url-preview', projectName: 'Alpha', urlId: 'u1' },
      { kind: 'project', projectName: 'Alpha' },
    ],
    ['diff carries no project context', { kind: 'diff', commitSha: 'abc' }, undefined],
    ['welcome carries no project context', { kind: 'welcome' }, undefined],
  ];

  for (const [name, origin, expected] of cases) {
    it(name, () => {
      expect(resolveTaskTabScope(origin)).toEqual(expected);
    });
  }

  it('returns undefined when there is no origin tab (empty editor)', () => {
    expect(resolveTaskTabScope(null)).toBeUndefined();
  });

  it('inherits the origin task tab scope so a chain of opens never drifts', () => {
    const origin: TaskTab = { kind: 'task', taskKey: 'w::a', scope: { kind: 'all-projects' } };
    expect(resolveTaskTabScope(origin)).toEqual({ kind: 'all-projects' });
  });

  it('leaves an unstamped origin task tab unknown rather than guessing', () => {
    expect(resolveTaskTabScope({ kind: 'task', taskKey: 'w::a' })).toBeUndefined();
  });
});

describe('taskTabProjectScope (active task tab → app scope)', () => {
  const lookup = (taskKey: string) => (taskKey === 'w::a' ? 'Alpha' : undefined);

  it('reports "All projects" for a task opened from the cross-project board', () => {
    const tab: TaskTab = { kind: 'task', taskKey: 'w::a', scope: { kind: 'all-projects' } };
    // Null = workspace-wide, even though the task itself belongs to Alpha.
    expect(taskTabProjectScope(tab, lookup)).toBeNull();
  });

  it('reports the origin project for a task opened from a project board', () => {
    const tab: TaskTab = { kind: 'task', taskKey: 'w::a', scope: { kind: 'project', projectName: 'Beta' } };
    expect(taskTabProjectScope(tab, lookup)).toBe('Beta');
  });

  it('falls back to the task project when the tab carries no stamp', () => {
    expect(taskTabProjectScope({ kind: 'task', taskKey: 'w::a' }, lookup)).toBe('Alpha');
  });

  it('stays undefined when neither the stamp nor the job feed knows the project', () => {
    expect(taskTabProjectScope({ kind: 'task', taskKey: 'w::missing' }, lookup)).toBeUndefined();
  });
});

describe('normalizeTaskTabScope (persisted payload guard)', () => {
  it('keeps a workspace-wide stamp', () => {
    expect(normalizeTaskTabScope({ kind: 'all-projects' })).toEqual({ kind: 'all-projects' });
  });

  it('keeps a named project stamp', () => {
    expect(normalizeTaskTabScope({ kind: 'project', projectName: 'Alpha' }))
      .toEqual({ kind: 'project', projectName: 'Alpha' });
  });

  it('drops a project stamp with no name instead of scoping to nothing', () => {
    expect(normalizeTaskTabScope({ kind: 'project', projectName: '' })).toBeUndefined();
  });

  it('passes undefined through', () => {
    expect(normalizeTaskTabScope(undefined)).toBeUndefined();
  });
});
