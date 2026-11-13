import { describe, expect, it } from 'vitest';
import type { TaskInfo } from '../../models/task.model';
import { buildComposerLocationContext } from './composer-location-context';

const task = {
  id: 'agt-2162-folder',
  taskKey: 'C:/tasks/AGT-2162',
  displayKey: 'AGT-2162',
  projectName: 'Agent Studio',
  title: 'Fix task context',
  state: '3-progress',
  watchPath: 'C:/tasks',
} as TaskInfo;

describe('buildComposerLocationContext', () => {
  it('maps a project Board from the canonical active tab', () => {
    expect(buildComposerLocationContext(
      { kind: 'board', projectName: 'Agent Studio' },
      [task],
    )).toEqual({ project: 'Agent Studio', surface: 'Board' });
  });

  it('gives a persona tab the chat context persona:<id> and the Team page none', () => {
    expect(buildComposerLocationContext({ kind: 'team', personaId: 'warden-1' }, [])).toEqual({
      project: null, surface: 'Team', detail: 'warden-1', referenceKey: 'persona:warden-1',
    });
    expect(buildComposerLocationContext({ kind: 'team', personaId: null }, [])).toEqual({ project: null, surface: 'Team' });
  });

  it('maps a task tab with project and task identity', () => {
    expect(buildComposerLocationContext(
      { kind: 'task', taskKey: task.taskKey },
      [task],
    )).toEqual({
      project: 'Agent Studio',
      surface: 'Task',
      detail: 'AGT-2162',
      taskKey: 'AGT-2162',
      taskId: 'agt-2162-folder',
      taskTitle: 'Fix task context',
      taskState: '3-progress',
      taskWatchPath: 'C:/tasks',
    });
  });

  it.each([
    [{ kind: 'hub', projectName: 'Agent Studio' } as const, 'Deck'],
    [{ kind: 'hub', projectName: 'Agent Studio', section: 'overview' } as const, 'Overview'],
    [{ kind: 'hub', projectName: 'Agent Studio', section: 'wiki' } as const, 'Wiki'],
    [{ kind: 'hub', projectName: 'Agent Studio', section: 'project-urls' } as const, 'Project URLs'],
    [{ kind: 'hub', projectName: 'Agent Studio', section: 'drift' } as const, 'Drift'],
  ])('maps project-level tab %o to %s', (tab, surface) => {
    expect(buildComposerLocationContext(tab, [task]))
      .toEqual({ project: 'Agent Studio', surface });
  });

  it('maps a URL preview with project and URL identity', () => {
    expect(buildComposerLocationContext(
      { kind: 'url-preview', projectName: 'Agent Studio', urlId: 'studio-dev' },
      [task],
    )).toEqual({ project: 'Agent Studio', surface: 'URL preview', detail: 'studio-dev' });
  });

  it.each([
    [{
      kind: 'workbench', projectName: 'Agent Studio', workbenchId: 'routing',
      title: 'Routing', key: 'AGT-W34',
    } as const, 'Dossier'],
    [{ kind: 'workbenches', projectName: 'Agent Studio' } as const, 'Dossiers'],
  ])('uses Dossier wording for repository document tab %o', (tab, surface) => {
    expect(buildComposerLocationContext(tab, [task]))
      .toEqual({
        project: 'Agent Studio',
        surface,
        ...(tab.kind === 'workbench' ? { detail: 'Routing', referenceKey: 'AGT-W34' } : {}),
      });
  });
});
