import { describe, expect, it, vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { of, throwError } from 'rxjs';

import { ReferencesSectionComponent } from './references-section.component';
import { TaskService } from '../../../../services/task.service';
import { NotificationService } from '../../../../services/notification.service';
import { TaskSelectionService } from '../../state/task-selection.service';
import { TaskState } from '../../../../models/task.model';
import type { TaskInfo, TaskReferenceLink, WaitsOnItem } from '../../../../models/task.model';
import { ProjectDocsService } from '../../../../services/project-docs.service';
import { StudioTabStateService } from '../../../studio-shell/services/studio-tab-state.service';

/**
 * AGT-2709: the operator affordance for the release gate.
 *
 * A `dependsOn` edge with `releaseGate: true` is only fulfilled once its target
 * is terminal AND carries the explicit `released` flag. Before this control the
 * flag had no UI at all, so a card could sit in Delivered looking finished
 * while its dependents silently stalled.
 *
 * This spec pins the two things that decide whether the operator finds it:
 * the visibility rules on BOTH sides of the gate (target card and dependent
 * card), and the service call each side issues.
 */
function makeTask(overrides: Partial<TaskInfo> = {}): TaskInfo {
  return {
    id: 'lib-acceptance',
    taskKey: 'lib::lib-acceptance',
    key: 'LIB-1',
    title: 'Library acceptance',
    state: TaskState.Completed,
    order: 1,
    agent: 'claude',
    createdAt: '2026-09-01T09:00:00Z',
    watchPath: '/ws/lib',
    projectName: 'LIB',
    folderPath: '/ws/lib/6-completed/lib-acceptance',
    lastActivity: '2026-09-01T09:30:00Z',
    cliType: 'claude',
    commits: [],
    ownerClientId: 'local-default',
    tags: [],
    references: { dependsOn: [], relatedTo: [], blockedBy: [], supersedes: [] },
    ...overrides,
  } as TaskInfo;
}

/** The consumer that waits on LIB-1 through a release-gated edge. */
const APP_1 = makeTask({
  id: 'app-consumer',
  taskKey: 'app::app-consumer',
  key: 'APP-1',
  title: 'Consumer rollout',
  state: TaskState.Ready,
  watchPath: '/ws/app',
  projectName: 'APP',
});

function gatedDependent(releaseGate: boolean): TaskReferenceLink {
  return {
    sourceKey: 'APP-1',
    sourceJobId: 'app-consumer',
    sourceTitle: 'Consumer rollout',
    sourceState: TaskState.Ready,
    sourceWatchPath: '/ws/app',
    kind: 'dependsOn',
    releaseGate,
  };
}

function waitsOnRelease(overrides: Partial<WaitsOnItem> = {}): WaitsOnItem {
  return {
    key: 'LIB-1',
    resolved: true,
    fulfilled: false,
    releaseGate: true,
    targetReleased: false,
    waitingForRelease: true,
    targetJobId: 'lib-acceptance',
    targetTitle: 'Library acceptance',
    targetState: TaskState.Completed,
    targetWatchPath: '/ws/lib',
    ...overrides,
  };
}

async function mount(
  info: TaskInfo,
  dependents: TaskReferenceLink[] = [],
  releaseResult = of({ released: true }),
) {
  const tasks = {
    jobs: signal<TaskInfo[]>([makeTask(), APP_1]),
    getTaskDependents: vi.fn().mockReturnValue(of(dependents)),
    setTaskReferences: vi.fn().mockReturnValue(of({ references: {}, warnings: [] })),
    setTaskReleased: vi.fn().mockReturnValue(releaseResult),
    refresh: vi.fn(),
  } as unknown as TaskService;
  const notifications = {
    info: vi.fn(), warning: vi.fn(), error: vi.fn(), success: vi.fn(),
  };

  await TestBed.configureTestingModule({
    imports: [ReferencesSectionComponent],
    providers: [
      provideZonelessChangeDetection(),
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
      { provide: TaskService, useValue: tasks },
      { provide: TaskSelectionService, useValue: { openDetail: vi.fn() } },
      { provide: NotificationService, useValue: notifications },
      {
        provide: ProjectDocsService,
        useValue: { getWorkbenches: vi.fn().mockReturnValue(of({ items: [] })) },
      },
      { provide: StudioTabStateService, useValue: { open: vi.fn() } },
    ],
  }).compileComponents();

  const fixture = TestBed.createComponent(ReferencesSectionComponent);
  fixture.componentRef.setInput('info', info);
  fixture.detectChanges();
  return { fixture, tasks, notifications };
}

describe('ReferencesSectionComponent release gate (target side)', () => {
  it('offers the release once the task is terminal and something gates on it', async () => {
    const { fixture } = await mount(makeTask(), [gatedDependent(true)]);
    const host: HTMLElement = fixture.nativeElement;

    expect(host.querySelector('[data-testid="references-row-release"]')).not.toBeNull();
    expect(host.querySelector('[data-testid="release-gate-state"]')!.textContent)
      .toContain('Release pending');
    expect(host.querySelector('[data-testid="release-gate-toggle"]')!.textContent)
      .toContain('Release for dependents');
    // The operator sees which card the decision unblocks without navigating.
    expect(host.querySelector('[data-testid="release-gate-dependents"]')!.textContent)
      .toContain('Unblocks APP-1');
  });

  it('stays hidden while the task has not reached a terminal lane', async () => {
    const { fixture } = await mount(
      makeTask({ state: TaskState.HumanReview }), [gatedDependent(true)]);
    expect(fixture.nativeElement.querySelector('[data-testid="references-row-release"]')).toBeNull();
  });

  it('stays hidden when the dependents do not opt into the release gate', async () => {
    const { fixture } = await mount(makeTask(), [gatedDependent(false)]);
    expect(fixture.nativeElement.querySelector('[data-testid="references-row-release"]')).toBeNull();
  });

  it('releases through the task service and re-pulls both views', async () => {
    const { fixture, tasks, notifications } = await mount(makeTask(), [gatedDependent(true)]);
    const changed = vi.fn();
    fixture.componentInstance.changed.subscribe(changed);

    (fixture.nativeElement.querySelector(
      '[data-testid="release-gate-toggle"]') as HTMLButtonElement).click();

    expect(tasks.setTaskReleased).toHaveBeenCalledWith('lib-acceptance', true, '/ws/lib');
    expect(notifications.success).toHaveBeenCalledWith('LIB-1 released, unblocking APP-1.');
    expect(tasks.refresh).toHaveBeenCalledWith(true);
    expect(changed).toHaveBeenCalledTimes(1);
  });

  it('is reversible: an already released task offers a withdrawal', async () => {
    const { fixture, tasks } = await mount(
      makeTask({ released: true }), [gatedDependent(true)], of({ released: false }));
    const host: HTMLElement = fixture.nativeElement;

    expect(host.querySelector('[data-testid="release-gate-state"]')!.textContent)
      .toContain('Released');
    const toggle = host.querySelector('[data-testid="release-gate-toggle"]') as HTMLButtonElement;
    expect(toggle.textContent).toContain('Withdraw release');

    toggle.click();
    expect(tasks.setTaskReleased).toHaveBeenCalledWith('lib-acceptance', false, '/ws/lib');
  });

  it('reports a failed write instead of pretending the gate opened', async () => {
    const { fixture, tasks, notifications } = await mount(
      makeTask(), [gatedDependent(true)], throwError(() => new Error('boom')));
    const changed = vi.fn();
    fixture.componentInstance.changed.subscribe(changed);

    (fixture.nativeElement.querySelector(
      '[data-testid="release-gate-toggle"]') as HTMLButtonElement).click();

    expect(notifications.error).toHaveBeenCalled();
    expect(changed).not.toHaveBeenCalled();
    expect(tasks.refresh).not.toHaveBeenCalled();
  });
});

describe('ReferencesSectionComponent release gate (dependent side)', () => {
  const dependentInfo = () => makeTask({
    id: 'app-consumer',
    taskKey: 'app::app-consumer',
    key: 'APP-1',
    title: 'Consumer rollout',
    state: TaskState.Ready,
    watchPath: '/ws/app',
    projectName: 'APP',
    references: {
      dependsOn: [{ key: 'LIB-1', releaseGate: true }],
      relatedTo: [], blockedBy: [], supersedes: [],
    },
    waitsOn: { items: [waitsOnRelease()], blocked: true, cycleDetected: false },
  });

  it('releases the target inline, from the dependent card', async () => {
    const { fixture, tasks } = await mount(dependentInfo());
    const host: HTMLElement = fixture.nativeElement;

    const row = host.querySelector('[data-testid="references-row-dependsOn"]')!;
    expect(row.querySelector('[data-testid="release-gate-action"]')).not.toBeNull();

    (row.querySelector('[data-testid="release-gate-toggle"]') as HTMLButtonElement).click();

    // The waits-on overlay resolved the target's job id and watch path, so the
    // write lands on LIB-1 in its own project without a second lookup.
    expect(tasks.setTaskReleased).toHaveBeenCalledWith('lib-acceptance', true, '/ws/lib');
  });

  it('offers nothing inline while the target is merely waiting for completion', async () => {
    const info = makeTask({
      id: 'app-consumer',
      key: 'APP-1',
      state: TaskState.Ready,
      watchPath: '/ws/app',
      references: {
        dependsOn: [{ key: 'LIB-1', releaseGate: true }],
        relatedTo: [], blockedBy: [], supersedes: [],
      },
      waitsOn: {
        items: [waitsOnRelease({
          waitingForRelease: false,
          targetState: TaskState.HumanReview,
        })],
        blocked: true,
        cycleDetected: false,
      },
    });
    const { fixture } = await mount(info);
    expect(fixture.nativeElement.querySelector('[data-testid="release-gate-action"]')).toBeNull();
  });
});
