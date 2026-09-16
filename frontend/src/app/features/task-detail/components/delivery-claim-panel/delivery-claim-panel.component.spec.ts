import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { describe, expect, it, vi } from 'vitest';
import { of, throwError } from 'rxjs';
import { DeliveryClaimPanelComponent } from './delivery-claim-panel.component';
import { TaskService } from '../../../../services/task.service';
import {
  TaskState,
  type TaskDeliveryClaimAnswer,
  type TaskInfo,
} from '../../../../models/task.model';

function answer(overrides: Partial<TaskDeliveryClaimAnswer> = {}): TaskDeliveryClaimAnswer {
  return {
    taskKey: 'agt::task',
    jobId: 'task',
    lane: TaskState.Completed,
    deliveryRef: 'task/agt-2706',
    integrationBranch: 'develop',
    releaseBranch: 'main',
    containmentStatus: 'integrated',
    integrated: true,
    released: true,
    integratedSha: '79c2dcf8c',
    mergeCommit: '9cfc0e074ab',
    mergeSubject: 'merge(AGT-2706): integrate reviewed delivery (operator card-scoped merge)',
    mergedAt: '2026-09-07T21:31:00Z',
    hasIntegrationRecord: false,
    class: 'integrated-delivery',
    findings: ['missing-integration-record'],
    completionClaim: null,
    commits: [],
    detail: null,
    ...overrides,
  };
}

function job(overrides: Partial<TaskInfo> = {}): TaskInfo {
  return {
    id: 'task',
    taskKey: 'agt::task',
    key: 'AGT-2706',
    title: 'Archive before integration',
    state: TaskState.Completed,
    order: 1,
    agent: 'claude',
    createdAt: '2026-09-07T10:00:00Z',
    watchPath: '/workspace/tasks',
    projectName: 'PROJ-002',
    folderPath: '/workspace/tasks/6-completed/task',
    lastActivity: '2026-09-07T21:31:00Z',
    sessionName: null,
    model: null,
    cliType: 'claude',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    ...overrides,
  } as TaskInfo;
}

async function mount(info: TaskInfo, getDeliveryClaim = vi.fn(() => of(answer()))) {
  TestBed.resetTestingModule();
  await TestBed.configureTestingModule({
    imports: [DeliveryClaimPanelComponent],
    providers: [
      provideZonelessChangeDetection(),
      { provide: TaskService, useValue: { getDeliveryClaim } },
    ],
  }).compileComponents();
  const fixture = TestBed.createComponent(DeliveryClaimPanelComponent);
  fixture.componentRef.setInput('job', info);
  fixture.detectChanges();
  return fixture;
}

describe('DeliveryClaimPanelComponent', () => {
  /**
   * AGT-2706: contained in develop and in main, integrated by an operator
   * card-scoped merge. The card now says so where the operator looks, and
   * names the merge that carried it.
   */
  it('states the delivery, its containment, the merge, and the release', async () => {
    const fixture = await mount(job());
    const host = fixture.nativeElement as HTMLElement;

    expect(host.querySelector('[data-testid="delivery-claim-ref"]')?.textContent).toContain('task/agt-2706');
    expect(host.querySelector('[data-testid="delivery-claim-integration"]')?.textContent).toContain('In develop');
    expect(host.querySelector('[data-testid="delivery-claim-merge"]')?.textContent).toContain('9cfc0e074');
    const release = host.querySelector('[data-testid="delivery-claim-release"]');
    expect(release?.textContent).toContain('In main');
    expect(release?.getAttribute('data-state')).toBe('released');
  });

  it('says "not checked yet" instead of pending when containment is unanswered', async () => {
    const fixture = await mount(
      job(),
      vi.fn(() => of(answer({ integrated: false, containmentStatus: 'unknown', released: null }))),
    );
    const host = fixture.nativeElement as HTMLElement;
    const integration = host.querySelector('[data-testid="delivery-claim-integration"]');

    expect(integration?.textContent).toContain('Not checked yet');
    expect(integration?.textContent).not.toContain('pending');
    expect(host.querySelector('[data-testid="delivery-claim-release"]')?.textContent)
      .toContain('Release not checked');
  });

  it('marks a requeued delivery as pending, never as replaced', async () => {
    const fixture = await mount(
      job(),
      vi.fn(() => of(answer({
        commits: [{
          sha: '79c2dcf8c',
          shortSha: '79c2dcf8c',
          onIntegrationBranch: true,
          onReleaseBranch: true,
          supersession: 'replacement-pending',
        }],
      }))),
    );
    const marker = (fixture.nativeElement as HTMLElement)
      .querySelector('[data-testid="delivery-claim-supersession"]');

    expect(marker?.textContent).toContain('Replacement pending');
    expect(marker?.getAttribute('data-state')).toBe('replacement-pending');
  });

  /** AGT-2795: the override's written reason travels with the completion claim. */
  it('shows an override reason wherever the card claims completion', async () => {
    const fixture = await mount(
      job(),
      vi.fn(() => of(answer({
        integrated: false,
        containmentStatus: 'pending',
        released: false,
        mergeCommit: null,
        completionClaim: {
          basis: 'operator-override',
          evidence: 'Completed by operator override.',
          reason: 'Delivery failed review and was abandoned; closing the card.',
          recordedAt: '2026-09-14T10:00:00Z',
        },
      }))),
    );
    const host = fixture.nativeElement as HTMLElement;

    expect(host.querySelector('[data-testid="delivery-claim-basis"]')?.textContent)
      .toContain('Completed by operator override');
    // Not yet released is the ordinary case between two releases, so it stays
    // quiet instead of borrowing the integration row's acute treatment.
    expect(host.querySelector('[data-testid="delivery-claim-release"]')?.getAttribute('data-state'))
      .toBe('unreleased');
    expect(host.querySelector('[data-testid="delivery-claim-override-reason"]')?.textContent)
      .toContain('Delivery failed review and was abandoned');
    expect(host.querySelector('[data-testid="delivery-claim-panel"]')?.getAttribute('data-integration'))
      .toBe('not-integrated');
  });

  it('falls back to the board projection when the lookup fails', async () => {
    const fixture = await mount(
      job({
        integration: {
          status: 'integrated',
          deliveryRef: 'task/agt-2706',
          sha: '79c2dcf8c',
          integrationBranch: 'develop',
          detail: 'anchor-ancestor',
        },
      }),
      vi.fn(() => throwError(() => new Error('offline'))),
    );
    const host = fixture.nativeElement as HTMLElement;

    expect(host.querySelector('[data-testid="delivery-claim-integration"]')?.textContent).toContain('In develop');
    expect(host.querySelector('[data-testid="delivery-claim-release"]')?.textContent)
      .toContain('Release not checked');
  });

  it('renders nothing before a card reaches a delivered lane', async () => {
    const fixture = await mount(job({ state: TaskState.Progress }));

    expect((fixture.nativeElement as HTMLElement)
      .querySelector('[data-testid="delivery-claim-panel"]')).toBeNull();
  });
});
