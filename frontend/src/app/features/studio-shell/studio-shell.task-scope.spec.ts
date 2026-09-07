import { describe, expect, it } from 'vitest';
import { inheritedTaskScope, studioTabKey } from './studio-shell.types';

/**
 * Which origin surfaces hand an opened task the All-projects scope.
 *
 * The absent field is the default ("derive the scope from the task's own
 * project"), so these assertions distinguish `{}` from `{ originScope: ... }`
 * rather than checking a truthy value.
 */
describe('inheritedTaskScope', () => {
  it('inherits the All-projects scope from the cross-project board', () => {
    expect(inheritedTaskScope({ kind: 'board', projectName: '__all__' }))
      .toEqual({ originScope: 'all-projects' });
  });

  it('does not inherit a scope from a single-project board', () => {
    expect(inheritedTaskScope({ kind: 'board', projectName: 'Project A' })).toEqual({});
  });

  it('carries the All-projects scope across a task-to-task step (pager)', () => {
    expect(inheritedTaskScope({ kind: 'task', taskKey: 'C:/watch::task-a', originScope: 'all-projects' }))
      .toEqual({ originScope: 'all-projects' });
  });

  it('does not invent a scope when stepping off a project-scoped task', () => {
    expect(inheritedTaskScope({ kind: 'task', taskKey: 'C:/watch::task-a' })).toEqual({});
  });

  it('does not inherit a scope from a Deck tab or from no tab at all', () => {
    expect(inheritedTaskScope({ kind: 'hub', projectName: 'Project A' })).toEqual({});
    expect(inheritedTaskScope(null)).toEqual({});
    expect(inheritedTaskScope(undefined)).toEqual({});
  });

  it('keeps the origin scope out of the tab identity (one tab per task)', () => {
    // Same task reached from either surface is the same tab; the scope is
    // remembered state, not a second destination.
    expect(studioTabKey({ kind: 'task', taskKey: 'C:/watch::task-a', originScope: 'all-projects' }))
      .toBe(studioTabKey({ kind: 'task', taskKey: 'C:/watch::task-a' }));
  });
});
