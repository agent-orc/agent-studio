import { describe, expect, it, vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { of } from 'rxjs';

import { ReferencesSectionComponent } from './references-section.component';
import { TaskService } from '../../../../services/task.service';
import { NotificationService } from '../../../../services/notification.service';
import { TaskSelectionService } from '../../state/task-selection.service';
import { TaskState } from '../../../../models/task.model';
import type { TaskInfo, TaskReferenceLink } from '../../../../models/task.model';
import { ProjectDocsService } from '../../../../services/project-docs.service';
import { StudioTabStateService } from '../../../studio-shell/services/studio-tab-state.service';

/**
 * AGT-2029 render-path coverage for the detail-view dependency line.
 *
 * The generated smoke spec only proves the component instantiates; it does NOT
 * exercise the render path the operator sees. This one mounts the real
 * component with seeded inputs + stubbed service seams and asserts BOTH
 * dependency directions render from the detail view, which is the requirement
 * "im Task-Detail eine kleine Dependency-Zeile beide Richtungen (waits-on /
 * blocked-by-me)":
 *   - waits-on   -> the outgoing `dependsOn` chip, styled "waiting" while its
 *                   target has not reached completed/archive; and
 *   - blocked-by-me -> the incoming "Blocking" chip for a task that waits on
 *                   this one (the reverse index loaded via getTaskDependents).
 * It also pins the navigation wiring for the reverse chip.
 */
function makeTask(overrides: Partial<TaskInfo> = {}): TaskInfo {
  return {
    id: 'agt-audit',
    taskKey: 'agt::agt-audit',
    key: 'AGT-KOSTEN',
    title: 'Kosten-Audit',
    state: TaskState.Ready,
    order: 1,
    agent: 'claude',
    createdAt: '2026-07-09T09:00:00Z',
    watchPath: '/ws/agt',
    projectName: 'AGT',
    folderPath: '/ws/agt/2-ready/agt-audit',
    lastActivity: '2026-07-09T09:30:00Z',
    sessionName: null,
    model: null,
    cliType: 'claude',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    commits: [],
    ownerClientId: 'local-default',
    tags: [],
    references: { dependsOn: [], relatedTo: [], blockedBy: [], supersedes: [] },
    ...overrides,
  } as TaskInfo;
}

/** The cross-project pricing lib this audit waits on (still open -> "waiting"). */
const CAR_3 = makeTask({
  id: 'car-3',
  taskKey: 'car::car-3',
  key: 'CAR-3',
  title: 'Pricing lib',
  state: TaskState.Ready,
  watchPath: '/ws/car',
});

/** The web update that waits on THIS audit (the reverse / blocked-by-me edge). */
const WEB_UPDATE = makeTask({
  id: 'web-update',
  taskKey: 'web::web-update',
  key: 'WEB-UPDATE',
  title: 'Web pricing update',
  state: TaskState.Ready,
  watchPath: '/ws/web',
});

const INTERVENTION = makeTask({
  id: 'agt-2801',
  taskKey: 'agt::agt-2801',
  key: 'AGT-2801',
  title: 'Review toolchain unavailable',
  state: TaskState.Preparation,
});

const BLOCKING_LINK: TaskReferenceLink = {
  sourceKey: 'WEB-UPDATE',
  sourceJobId: 'web-update',
  sourceTitle: 'Web pricing update',
  sourceState: TaskState.Ready,
  sourceWatchPath: '/ws/web',
  kind: 'dependsOn',
};

function makeFakeTaskService(dependents: TaskReferenceLink[]) {
  return {
    jobs: signal<TaskInfo[]>([CAR_3, WEB_UPDATE, INTERVENTION]),
    getTaskDependents: vi.fn().mockReturnValue(of(dependents)),
    setTaskReferences: vi.fn().mockReturnValue(of({ references: {}, warnings: [] })),
  } as unknown as TaskService;
}

async function mount(info: TaskInfo, dependents: TaskReferenceLink[] = [BLOCKING_LINK]) {
  const tasks = makeFakeTaskService(dependents);
  const openDetail = vi.fn();
  const openTab = vi.fn();
  await TestBed.configureTestingModule({
    imports: [ReferencesSectionComponent],
    providers: [
      provideZonelessChangeDetection(),
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
      { provide: TaskService, useValue: tasks },
      { provide: TaskSelectionService, useValue: { openDetail } },
      { provide: NotificationService, useValue: { info: vi.fn(), warning: vi.fn(), error: vi.fn() } },
      {
        provide: ProjectDocsService,
        useValue: {
          getWorkbenches: vi.fn().mockReturnValue(of({
            projectName: 'AGT',
            includesHistory: true,
            count: 1,
            items: [{
              id: 'reference-source',
              key: 'AGT-W4',
              title: 'Reference source',
              summary: 'A linked source.',
              status: 'active',
              phase: 'testing',
              updatedAtUtc: '2026-08-09T10:00:00Z',
              entryPath: 'docs/operations/reference-source/index.html',
              valid: true,
              error: null,
              sourceTaskKeys: [],
              relatedTaskKeys: [],
            }],
          })),
        },
      },
      { provide: StudioTabStateService, useValue: { open: openTab } },
    ],
  }).compileComponents();

  const fixture = TestBed.createComponent(ReferencesSectionComponent);
  fixture.componentRef.setInput('info', info);
  fixture.detectChanges();
  return { fixture, tasks, openDetail, openTab };
}

describe('ReferencesSectionComponent (render — both dependency directions)', () => {
  it('shows the outgoing waits-on chip as "waiting" while its target is open', async () => {
    const info = makeTask({
      references: { dependsOn: ['CAR-3'], relatedTo: [], blockedBy: [], supersedes: [] },
    });
    const { fixture } = await mount(info, []);
    const host: HTMLElement = fixture.nativeElement;

    expect(host.querySelector('[data-testid="references-section"]')).not.toBeNull();
    expect(host.querySelector('[data-testid="references-row-dependsOn"]')).not.toBeNull();

    const chip = host.querySelector('[data-testid="reference-chip-CAR-3"]') as HTMLElement | null;
    expect(chip).not.toBeNull();
    expect(chip!.className).toContain('refs__chip--waiting');
    expect(chip!.textContent).toContain('CAR-3');
    // resolved against the workspace snapshot -> shows the target title too.
    expect(chip!.textContent).toContain('Pricing lib');
  });

  it('renders the reverse "Blocking" direction from the dependents index', async () => {
    const info = makeTask({
      references: { dependsOn: ['CAR-3'], relatedTo: [], blockedBy: [], supersedes: [] },
    });
    const { fixture, tasks } = await mount(info);
    const host: HTMLElement = fixture.nativeElement;

    // The reverse edge is loaded via the dependents endpoint for this task's key.
    expect(tasks.getTaskDependents).toHaveBeenCalledWith('agt-audit', 'dependsOn', '/ws/agt');

    const row = host.querySelector('[data-testid="references-row-blocking"]');
    expect(row).not.toBeNull();
    const blockingChip = host.querySelector('[data-testid="blocking-chip-WEB-UPDATE"]') as HTMLElement | null;
    expect(blockingChip).not.toBeNull();
    expect(blockingChip!.textContent).toContain('WEB-UPDATE');
    expect(blockingChip!.textContent).toContain('Web pricing update');
  });

  it('both directions are visible at once (waits-on and blocked-by-me)', async () => {
    const info = makeTask({
      references: { dependsOn: ['CAR-3'], relatedTo: [], blockedBy: [], supersedes: [] },
    });
    const { fixture } = await mount(info);
    const host: HTMLElement = fixture.nativeElement;

    expect(host.querySelector('[data-testid="reference-chip-CAR-3"]')).not.toBeNull();
    expect(host.querySelector('[data-testid="blocking-chip-WEB-UPDATE"]')).not.toBeNull();
  });

  it('shows a raised follow-up with its key, lane, and open state', async () => {
    const info = makeTask({
      references: {
        dependsOn: [], relatedTo: [], blockedBy: ['AGT-2801'], supersedes: [],
        raisedFollowUps: ['AGT-2801'],
      },
    });
    const { fixture } = await mount(info, []);
    const chip = fixture.nativeElement.querySelector(
      '[data-testid="references-row-raisedFollowUps"] [data-testid="reference-chip-AGT-2801"]',
    ) as HTMLElement | null;

    expect(chip?.textContent).toContain('AGT-2801 · Preparation · open');
  });

  it('clicking a blocking chip navigates to the task that waits on this one', async () => {
    const info = makeTask({
      references: { dependsOn: [], relatedTo: [], blockedBy: [], supersedes: [] },
    });
    const { fixture, openDetail } = await mount(info);
    const host: HTMLElement = fixture.nativeElement;

    // Even with no outgoing edges the section still renders for the reverse
    // direction, so the operator sees "who is waiting on me".
    const link = host.querySelector('[data-testid="blocking-chip-WEB-UPDATE"] .refs__chip-link') as HTMLButtonElement | null;
    expect(link).not.toBeNull();
    link!.click();

    expect(openDetail).toHaveBeenCalledTimes(1);
    expect(openDetail.mock.calls[0][0]).toMatchObject({ id: 'web-update', key: 'WEB-UPDATE' });
  });

  it('renders nothing when the task has neither outgoing refs nor dependents', async () => {
    const info = makeTask({
      references: { dependsOn: [], relatedTo: [], blockedBy: [], supersedes: [] },
    });
    const { fixture } = await mount(info, []);
    const host: HTMLElement = fixture.nativeElement;
    expect(host.querySelector('[data-testid="references-section"]')).toBeNull();
  });

  it('renders a linked document key and opens its viewer tab', async () => {
    const info = makeTask({
      references: {
        dependsOn: [], relatedTo: [], blockedBy: [], supersedes: [], workbenches: ['AGT-W4'],
      },
    });
    const { fixture, openTab } = await mount(info, []);
    const chip = fixture.nativeElement.querySelector(
      '[data-testid="reference-chip-AGT-W4"]') as HTMLElement;
    expect(chip.textContent).toContain('AGT-W4');
    expect(chip.textContent).toContain('Reference source');

    (chip.querySelector('.refs__chip-link') as HTMLButtonElement).click();
    expect(openTab).toHaveBeenCalledWith({
      kind: 'workbench',
      projectName: 'AGT',
      workbenchId: 'reference-source',
      title: 'Reference source',
    });
  });
});
