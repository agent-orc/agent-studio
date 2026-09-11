import { describe, expect, it, vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { of, throwError } from 'rxjs';

import { ReleaseGateSectionComponent } from './release-gate-section.component';
import { TaskService } from '../../../../services/task.service';
import { NotificationService } from '../../../../services/notification.service';
import { TaskSelectionService } from '../../state/task-selection.service';
import { TaskState } from '../../../../models/task.model';
import type { TaskInfo, TaskReferenceLink } from '../../../../models/task.model';

/**
 * AGT-2709 render-path coverage for the operator release affordance: the
 * visibility rules on both ends of a `releaseGate` edge, and that the button
 * actually reaches `TaskService.setTaskReleased` with the right target.
 */
const TARGET: TaskInfo = {
  id: 'agt-2372',
  taskKey: 'agt::agt-2372',
  key: 'AGT-2372',
  title: 'Release target',
  state: TaskState.Archive,
  released: false,
  order: 1,
  agent: 'claude',
  createdAt: '2026-09-01T09:00:00Z',
  watchPath: '/ws/agt',
  projectName: 'AGT',
  folderPath: '/ws/agt/7-archive/agt-2372',
  lastActivity: '2026-09-01T09:30:00Z',
  sessionName: null,
  tags: [],
} as unknown as TaskInfo;

const GATED_DEPENDENT: TaskReferenceLink = {
  sourceKey: 'AGT-2373',
  sourceJobId: 'agt-2373',
  sourceTitle: 'Dependent card',
  sourceState: TaskState.Ready,
  sourceWatchPath: '/ws/agt',
  kind: 'dependsOn',
  releaseGate: true,
};

/** The dependent's own view: complete target, release still missing. */
const DEPENDENT: TaskInfo = {
  ...TARGET,
  id: 'agt-2373',
  taskKey: 'agt::agt-2373',
  key: 'AGT-2373',
  title: 'Dependent card',
  state: TaskState.Ready,
  released: false,
  waitsOn: {
    blocked: true,
    cycleDetected: false,
    items: [{
      key: 'AGT-2372',
      resolved: true,
      fulfilled: false,
      releaseGate: true,
      targetReleased: false,
      waitingForRelease: true,
      targetJobId: 'agt-2372',
      targetTitle: 'Release target',
      targetState: TaskState.Archive,
      targetWatchPath: '/ws/agt',
    }],
  },
} as unknown as TaskInfo;

async function mount(
  info: TaskInfo,
  dependents: readonly TaskReferenceLink[] = [],
  setTaskReleased = vi.fn().mockReturnValue(of({ released: true })),
) {
  const notifications = { success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() };
  const openDetail = vi.fn();
  await TestBed.configureTestingModule({
    imports: [ReleaseGateSectionComponent],
    providers: [
      provideZonelessChangeDetection(),
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
      {
        provide: TaskService,
        useValue: { jobs: signal<TaskInfo[]>([TARGET, DEPENDENT]), setTaskReleased },
      },
      { provide: TaskSelectionService, useValue: { openDetail } },
      { provide: NotificationService, useValue: notifications },
    ],
  }).compileComponents();

  const fixture = TestBed.createComponent(ReleaseGateSectionComponent);
  fixture.componentRef.setInput('info', info);
  fixture.componentRef.setInput('dependents', dependents);
  const changed = vi.fn();
  fixture.componentInstance.changed.subscribe(changed);
  fixture.detectChanges();
  return { fixture, setTaskReleased, notifications, openDetail, changed };
}

describe('ReleaseGateSectionComponent', () => {
  it('offers the release action on a terminal task with a release-gated dependent', async () => {
    const { fixture } = await mount(TARGET, [GATED_DEPENDENT]);
    const host: HTMLElement = fixture.nativeElement;

    expect(host.querySelector('[data-testid="release-gate-target"]')).not.toBeNull();
    expect(host.querySelector('[data-testid="release-gate-state"]')!.textContent)
      .toContain('Release pending');
    expect(host.querySelector('[data-testid="release-gate-toggle"]')!.textContent)
      .toContain('Release for dependents');
    // The operator sees which dependent the decision unblocks.
    expect(host.querySelector('[data-testid="release-gate-dependent-AGT-2373"]')!.textContent)
      .toContain('AGT-2373');
  });

  it('renders nothing while the task is not terminal', async () => {
    const { fixture } = await mount({ ...TARGET, state: TaskState.Progress }, [GATED_DEPENDENT]);
    expect(fixture.nativeElement.querySelector('[data-testid="release-gate-section"]')).toBeNull();
  });

  it('renders nothing when no dependent gates on this task', async () => {
    const ungated = { ...GATED_DEPENDENT, releaseGate: false };
    const { fixture } = await mount(TARGET, [ungated]);
    expect(fixture.nativeElement.querySelector('[data-testid="release-gate-section"]')).toBeNull();
  });

  it('shows the withdraw direction once the flag is set', async () => {
    const { fixture } = await mount({ ...TARGET, released: true }, [GATED_DEPENDENT]);
    const host: HTMLElement = fixture.nativeElement;

    expect(host.querySelector('[data-testid="release-gate-state"]')!.textContent).toContain('Released');
    expect(host.querySelector('[data-testid="release-gate-toggle"]')!.textContent)
      .toContain('Withdraw release');
  });

  it('releases this task and asks the parent to re-fetch', async () => {
    const { fixture, setTaskReleased, changed } = await mount(TARGET, [GATED_DEPENDENT]);
    const host: HTMLElement = fixture.nativeElement;

    host.querySelector<HTMLButtonElement>('[data-testid="release-gate-toggle"]')!.click();

    expect(setTaskReleased).toHaveBeenCalledWith('agt-2372', true, '/ws/agt');
    expect(changed).toHaveBeenCalledTimes(1);
  });

  it('withdraws the release when the flag is already set', async () => {
    const { fixture, setTaskReleased } = await mount(
      { ...TARGET, released: true }, [GATED_DEPENDENT],
    );
    fixture.nativeElement
      .querySelector('[data-testid="release-gate-toggle"]')
      .click();

    expect(setTaskReleased).toHaveBeenCalledWith('agt-2372', false, '/ws/agt');
  });

  it('releases the waited-for target inline from the dependent card', async () => {
    const { fixture, setTaskReleased, changed } = await mount(DEPENDENT);
    const host: HTMLElement = fixture.nativeElement;

    expect(host.querySelector('[data-testid="release-gate-waiting-AGT-2372"]')).not.toBeNull();
    host.querySelector<HTMLButtonElement>('[data-testid="release-gate-release-AGT-2372"]')!.click();

    // Addressed at the TARGET's folder id and watch path, not the dependent's.
    expect(setTaskReleased).toHaveBeenCalledWith('agt-2372', true, '/ws/agt');
    expect(changed).toHaveBeenCalledTimes(1);
  });

  it('keeps the flag unchanged and reports a failed write', async () => {
    const failing = vi.fn().mockReturnValue(throwError(() => new Error('boom')));
    const { fixture, notifications, changed } = await mount(TARGET, [GATED_DEPENDENT], failing);
    fixture.nativeElement.querySelector('[data-testid="release-gate-toggle"]').click();

    expect(notifications.error).toHaveBeenCalled();
    expect(changed).not.toHaveBeenCalled();
  });
});
