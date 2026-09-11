import { afterEach, describe, expect, it, vi } from 'vitest';
import type { StudioTab } from '../studio-shell.types';
import {
  parseStudioRoute,
  navigateStudioRoute,
  replaceStudioRouteQuery,
  replaceTaskViewRoute,
  studioRouteForTab,
} from './studio-route';

describe('Studio route contract', () => {
  afterEach(() => {
    vi.restoreAllMocks();
    history.replaceState(null, '', '/');
  });

  it('parses every state-bearing primary surface', () => {
    expect(parseStudioRoute('#/board&filters=type%3Abug')).toEqual({
      kind: 'board',
      projectSlug: null,
    });
    expect(parseStudioRoute('#/feed')).toEqual({ kind: 'feed' });
    expect(parseStudioRoute('#/chat-history')).toEqual({ kind: 'chat-history' });
    expect(parseStudioRoute('#/projects/agent-studio/board')).toEqual({
      kind: 'board',
      projectSlug: 'agent-studio',
    });
    expect(parseStudioRoute('#/projects/agent-studio')).toEqual({
      kind: 'hub',
      projectSlug: 'agent-studio',
      section: 'overview',
      page: null,
      folder: null,
    });
    expect(parseStudioRoute('#/projects/agent-studio/wiki?page=concepts%2Frouting.md')).toEqual({
      kind: 'hub',
      projectSlug: 'agent-studio',
      section: 'wiki',
      page: 'concepts/routing.md',
      folder: null,
    });
    expect(parseStudioRoute('#/projects/agent-studio/workbenches/route-lab')).toEqual({
      kind: 'workbench',
      projectSlug: 'agent-studio',
      workbenchId: 'route-lab',
    });
    expect(parseStudioRoute('#/projects/agent-studio/workbenches')).toEqual({
      kind: 'workbenches',
      projectSlug: 'agent-studio',
    });
    expect(parseStudioRoute('#/workbenches')).toEqual({
      kind: 'workbenches',
      projectSlug: null,
    });
    expect(parseStudioRoute('#/tasks/AGT-2291?view=timeline%3Aactivity')).toEqual({
      kind: 'task',
      reference: 'AGT-2291',
      tab: 'timeline',
      inspector: 'activity',
    });
    expect(parseStudioRoute('#/projects/agent-studio/epics')).toEqual({
      kind: 'epics',
      projectSlug: 'agent-studio',
    });
    expect(parseStudioRoute('#/epics')).toEqual({
      kind: 'epics',
      projectSlug: null,
    });
    expect(parseStudioRoute('#/epics/AGT-2200')).toEqual({
      kind: 'epic',
      reference: 'AGT-2200',
    });
    expect(parseStudioRoute('#/workspace/settings/task-server')).toEqual({
      kind: 'workspace-settings',
      section: 'task-server',
      detail: null,
    });
    expect(parseStudioRoute('#/workspace/settings/tokens/codex')).toEqual({
      kind: 'workspace-settings',
      section: 'tokens',
      detail: 'codex',
    });
  });

  it('builds one hash-path pattern for tabs', () => {
    const cases: [StudioTab, string | null, string][] = [
      [{ kind: 'board', projectName: '__all__' }, null, '/board'],
      [{ kind: 'feed' }, null, '/feed'],
      [{ kind: 'chat-history' }, null, '/chat-history'],
      [{ kind: 'board', projectName: 'Agent Studio' }, null, '/projects/agent-studio/board'],
      [{ kind: 'hub', projectName: 'Agent Studio', section: 'wiki' }, null, '/projects/agent-studio/wiki'],
      [
        { kind: 'workbench', projectName: 'Agent Studio', projectId: 'PROJ-002', workbenchId: 'route lab' },
        null,
        '/projects/PROJ-002/workbenches/route%20lab',
      ],
      [{ kind: 'workbenches', projectName: null }, null, '/workbenches'],
      [{ kind: 'workbenches', projectName: 'Agent Studio' }, null, '/projects/agent-studio/workbenches'],
      [{ kind: 'task', taskKey: 'private::task' }, 'AGT-2291', '/tasks/AGT-2291'],
      [{ kind: 'epics', projectName: null }, null, '/epics'],
      [{ kind: 'epics', projectName: 'Agent Studio' }, null, '/projects/agent-studio/epics'],
      [{ kind: 'epic', epicKey: 'private::epic' }, 'AGT-2200', '/epics/AGT-2200'],
    ];
    for (const [tab, reference, expected] of cases) {
      expect(studioRouteForTab(tab, reference)).toBe(expected);
    }
  });

  it('preserves route-local query on the same surface', () => {
    history.replaceState(null, '', '/#/projects/agent-studio/wiki?page=concepts%2Frouting.md&filters=x');
    const replace = vi.spyOn(history, 'replaceState');
    const push = vi.spyOn(history, 'pushState');

    navigateStudioRoute('/projects/agent-studio/wiki');

    expect(replace).not.toHaveBeenCalled();
    expect(push).not.toHaveBeenCalled();
    expect(location.hash).toContain('page=concepts%2Frouting.md');

    replaceStudioRouteQuery({ page: 'README.md', folder: null });
    expect(replace).toHaveBeenCalledTimes(1);
    expect(location.hash).toBe('#/projects/agent-studio/wiki?page=README.md&filters=x');
  });

  it('adds history entries between project and All Projects boards', () => {
    history.replaceState(null, '', '/#/projects/agent-studio/board&filters=x');
    const replace = vi.spyOn(history, 'replaceState');
    const push = vi.spyOn(history, 'pushState');

    navigateStudioRoute('/board');

    expect(push).toHaveBeenCalledTimes(1);
    expect(replace).not.toHaveBeenCalled();
    expect(location.hash).toBe('#/board&filters=x');

    navigateStudioRoute('/projects/agent-studio/board');

    expect(push).toHaveBeenCalledTimes(2);
    expect(location.hash).toBe('#/projects/agent-studio/board&filters=x');
  });

  it('canonicalizes a route-less cold boot without adding a history entry', () => {
    history.replaceState(null, '', '/');
    const replace = vi.spyOn(history, 'replaceState');
    const push = vi.spyOn(history, 'pushState');

    navigateStudioRoute('/board');

    expect(replace).toHaveBeenCalledTimes(1);
    expect(push).not.toHaveBeenCalled();
    expect(location.hash).toBe('#/board');
  });

  it('round-trips Task detail and inspector tab state without adding history', () => {
    history.replaceState(null, '', '/#/tasks/AGT-2291&filters=x');
    const replace = vi.spyOn(history, 'replaceState');

    replaceTaskViewRoute('code-review', 'activity');

    expect(replace).toHaveBeenCalledTimes(1);
    expect(location.hash).toBe('#/tasks/AGT-2291?view=code-review%3Aactivity&filters=x');
    expect(parseStudioRoute(location.hash)).toEqual({
      kind: 'task',
      reference: 'AGT-2291',
      tab: 'code-review',
      inspector: 'activity',
    });
  });

  it('round-trips the Task inspector tab', () => {
    const parsed = parseStudioRoute('#/tasks/AGT-2408?view=overview%3Atask');
    expect(parsed).toEqual({
      kind: 'task',
      reference: 'AGT-2408',
      tab: 'overview',
      inspector: 'task',
    });
  });

  it('falls back to Result for a legacy Task Chat inspector route', () => {
    const parsed = parseStudioRoute('#/tasks/AGT-2574?view=overview%3Achat');
    expect(parsed).toEqual({
      kind: 'task',
      reference: 'AGT-2574',
      tab: 'overview',
      inspector: 'protocol',
    });
  });
});
