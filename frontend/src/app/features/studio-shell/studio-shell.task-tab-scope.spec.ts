import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { beforeEach, describe, expect, it } from 'vitest';
import { StudioShellComponent } from './studio-shell.component';
import { StudioTabStateService } from './services/studio-tab-state.service';
import { TaskService } from '../../services/task.service';
import type { GroupedJobs, TaskInfo } from '../../models/task.model';

/**
 * AGT-2692, operator sighting (2026-08-29): opening a task from the
 * "All projects" board switched the whole workspace into that task's single
 * project, so the picker, the Explorer marker, and the board behind the tab
 * all narrowed and the operator was stranded in one project on close.
 *
 * Opening a task is navigation into a detail, not a project switch. The
 * shell's `currentProjectName` / `activeProjectName` are the *app scope*
 * (picker label, Explorer "all projects" marker, sidebar CTAs); the detail's
 * own project handle is resolved separately by `TaskSelectionService`.
 */

const STORAGE_KEY = 'atp.studio.tabs.v1';

function task(id: string, projectName: string): TaskInfo {
  return {
    id,
    taskKey: `${projectName}::${id}`,
    title: id,
    state: '5-human-review',
    order: 1,
    agent: 'claude',
    createdAt: '2026-08-29T08:00:00Z',
    lastActivity: '2026-08-29T09:00:00Z',
    watchPath: `C:/proj/${projectName}`,
    projectName,
    folderPath: `C:/proj/${projectName}/${id}`,
    epicId: null,
  } as TaskInfo;
}

function groupedWith(...jobs: TaskInfo[]): GroupedJobs {
  return {
    backlog: [], preparation: [], orchestratorPrep: [], ready: [],
    progress: [], failedPickup: [], codeNotComplete: [], autoReview: [],
    humanReview: jobs, escalated: [], review: [], completed: [], archive: [],
  } as GroupedJobs;
}

const ALPHA_TASK = task('AGT-1', 'Alpha');
const BETA_TASK = task('AGT-2', 'Beta');

describe('StudioShellComponent · task opened from the All-projects board', () => {
  let component: StudioShellComponent;
  let tabs: StudioTabStateService;

  beforeEach(() => {
    localStorage.removeItem(STORAGE_KEY);
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      imports: [StudioShellComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    });
    const fixture = TestBed.createComponent(StudioShellComponent);
    component = fixture.componentInstance;
    tabs = TestBed.inject(StudioTabStateService);
    TestBed.inject(TaskService).grouped.set(groupedWith(ALPHA_TASK, BETA_TASK));
    fixture.componentRef.setInput('knownProjectNames', ['Alpha', 'Beta']);
  });

  it('keeps the active scope on All projects', () => {
    component.openBoard('__all__');
    expect(component.activeProjectName()).toBeNull();

    component.openTask(BETA_TASK);

    expect(tabs.activeTab()).toMatchObject({ kind: 'task', taskKey: BETA_TASK.taskKey });
    expect(component.currentProjectName()).toBeNull();
    expect(component.activeProjectName()).toBeNull();
    expect(component.activeProjectPickerLabel()).toBe('All projects');
  });

  it('closing the task lands back on the All-projects board, still unscoped', () => {
    component.openBoard('__all__');
    component.openTask(BETA_TASK);

    component.closeTab(`task:${BETA_TASK.taskKey}`);

    expect(tabs.activeTab()).toEqual({ kind: 'board', projectName: '__all__' });
    expect(component.activeProjectName()).toBeNull();
  });

  it('still scopes to the project when the task is opened from that project board', () => {
    component.openBoard('Alpha');
    component.openTask(ALPHA_TASK);

    expect(component.currentProjectName()).toBe('Alpha');
    expect(component.activeProjectPickerLabel()).toBe('Alpha');
  });

  it('falls back to the task project for a tab with no recorded origin', () => {
    // Pre-AGT-2692 snapshot / deep link: no scope on the tab, so the shell
    // keeps the old behaviour instead of guessing "All projects".
    tabs.closeAll();
    tabs.open({ kind: 'task', taskKey: BETA_TASK.taskKey });

    expect(component.currentProjectName()).toBe('Beta');
  });
});
