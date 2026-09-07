import '@angular/compiler';
import { Injector, runInInjectionContext, signal } from '@angular/core';
import { describe, expect, it } from 'vitest';
import type { TaskInfo } from '../models/task.model';
import { TaskSelectionService } from '../features/task-detail/state/task-selection.service';
import { StudioTabStateService } from '../features/studio-shell/services/studio-tab-state.service';
import { TaskService } from './task.service';
import { TaskReferenceNavigationService } from './task-reference-navigation.service';

describe('TaskReferenceNavigationService', () => {
  function task(overrides: Partial<TaskInfo>): TaskInfo {
    return {
      id: 'feature-clickable-task-references-open-task-tab',
      taskKey: 'C:/Projects/demo::feature-clickable-task-references-open-task-tab',
      key: 'ASS-738',
      title: 'Clickable task references',
      state: '3-progress',
      order: 0,
      agent: 'codex',
      createdAt: '',
      watchPath: 'C:/Projects/demo',
      projectName: 'demo',
      folderPath: 'C:/workspace/projects/demo/3-progress/feature-clickable-task-references-open-task-tab',
      lastActivity: '',
      sessionName: null,
      model: null,
      cliType: 'codex',
      useOwnSession: null,
      lastUsage: null,
      execution: null,
      commit: null,
      ...overrides,
    };
  }

  function setup(jobs: TaskInfo[]) {
    const jobsSignal = signal(jobs);
    const openedTabs: unknown[] = [];
    const openedDetails: TaskInfo[] = [];

    const injector = Injector.create({
      providers: [
        { provide: TaskService, useValue: { jobs: jobsSignal } },
        {
          provide: StudioTabStateService,
          // A markdown reference opens through the scope-preserving door so a
          // task reached from the All-projects board does not switch the
          // workspace into that task's project (AGT-2692).
          useValue: {
            open: (tab: unknown) => openedTabs.push(tab),
            openTaskFromCurrentScope: (taskKey: string) => openedTabs.push({ kind: 'task', taskKey }),
          },
        },
        { provide: TaskSelectionService, useValue: { openDetail: (job: TaskInfo) => openedDetails.push(job) } },
      ],
    });

    return {
      service: runInInjectionContext(
        injector,
        () => new TaskReferenceNavigationService(),
      ),
      jobsSignal,
      openedTabs,
      openedDetails,
    };
  }

  it('publishes ASS keys, task ids, task-key tails, and folder slugs as markdown labels', () => {
    const { service } = setup([
      task({
        id: 'internal-123',
        taskKey: 'C:/Projects/demo::internal-123',
        key: 'ASS-740',
        folderPath: 'C:\\workspace\\projects\\demo\\3-progress\\human-visible-folder-slug',
      }),
    ]);

    const labels = service.markdownReferences().map(ref => ref.label);

    expect(labels).toContain('ASS-740');
    expect(labels).toContain('internal-123');
    expect(labels).toContain('human-visible-folder-slug');
  });

  it('keeps the reference catalogue stable across equivalent task polls', () => {
    const original = task({ key: 'ASS-740' });
    const { service, jobsSignal } = setup([original]);
    const first = service.markdownReferences();

    jobsSignal.set([{ ...original }]);

    expect(service.markdownReferences()).toBe(first);
  });

  it('opens a known task reference through the existing task tab and detail flow', () => {
    const known = task({ taskKey: 'C:/Projects/demo::known-task' });
    const { service, openedTabs, openedDetails } = setup([known]);

    expect(service.openTaskKey('C:/Projects/demo::known-task')).toBe(true);

    expect(openedTabs).toEqual([{ kind: 'task', taskKey: 'C:/Projects/demo::known-task' }]);
    expect(openedDetails).toEqual([known]);
  });

  it('ignores missing task references without opening a tab', () => {
    const { service, openedTabs, openedDetails } = setup([task({})]);

    expect(service.openTaskKey('C:/Projects/demo::missing-task')).toBe(false);

    expect(openedTabs).toEqual([]);
    expect(openedDetails).toEqual([]);
  });
});
