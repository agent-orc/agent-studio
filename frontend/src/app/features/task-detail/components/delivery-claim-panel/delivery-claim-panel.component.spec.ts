import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { describe, expect, it, vi } from 'vitest';
import { of, throwError } from 'rxjs';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { readinessItem } from '../../../acceptance-readiness/testing/acceptance-readiness.fixtures';
import { DeliveryClaimPanelComponent } from './delivery-claim-panel.component';
import { TaskService } from '../../../../services/task.service';
import { NotificationService } from '../../../../services/notification.service';
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

const conceptReview: NonNullable<TaskDeliveryClaimAnswer['contentReview']> = {
  mode: 'concept',
  currentBriefVersion: 'bbbbbbbbbbbbbbbb',
  deliveryBriefVersion: 'aaaaaaaaaaaaaaaa',
  reviewBriefVersion: 'aaaaaaaaaaaaaaaa',
  requiredAspect: 'concept-fit',
  contentStatus: 'block',
  contentSummary: 'The Dossier contradicts the selected direction.',
  evidenceChecked: 'Brief: keep the current workflow.',
  missing: 'Dossier: replace the workflow.',
  ranAspects: ['build-tests', 'lint', 'concept-fit'],
  skippedAspects: ['documentation-impact'],
};

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

async function mount(info: TaskInfo, getDeliveryClaim = vi.fn(() => of(answer())),
  tasks: Record<string, unknown> = {}) {
  TestBed.resetTestingModule();
  await TestBed.configureTestingModule({
    imports: [DeliveryClaimPanelComponent],
    providers: [
      provideZonelessChangeDetection(),
      provideHttpClient(),
      provideHttpClientTesting(),
      { provide: TaskService, useValue: { getDeliveryClaim, ...tasks } },
      { provide: NotificationService, useValue: { success: vi.fn() } },
    ],
  }).compileComponents();
  const fixture = TestBed.createComponent(DeliveryClaimPanelComponent);
  fixture.componentRef.setInput('job', info);
  fixture.detectChanges();
  return fixture;
}

describe('DeliveryClaimPanelComponent', () => {
  it('shows current brief, concept citations, contradictions, and skipped aspects in Human Review', async () => {
    const fixture = await mount(
      job({ state: TaskState.HumanReview }),
      vi.fn(() => of(answer({ contentReview: conceptReview }))));
    const host = fixture.nativeElement as HTMLElement;
    const grounds = host.querySelector('[data-testid="completion-content-review"]');
    expect(grounds?.getAttribute('data-state')).toBe('gap');
    expect(grounds?.textContent).toContain('Delivery brief aaaaaaaaaaaa');
    expect(grounds?.textContent).toContain('Current brief bbbbbbbbbbbb');
    expect(grounds?.textContent).toContain('Skipped: documentation-impact');
    expect(host.querySelector('[data-testid="concept-fit-comparison"]')?.textContent)
      .toContain('Dossier: replace the workflow.');
    expect(grounds?.textContent).toContain('Contradiction or gap');
    expect(host.querySelector('[data-testid="stale-brief-acceptance"]')?.textContent)
      .toContain('Acceptance is blocked');
  });
  describe('AGT-3874 accept with a reason on a content verdict', () => {
    const concerns: NonNullable<TaskDeliveryClaimAnswer['contentReview']> = {
      mode: 'coding',
      currentBriefVersion: 'aaaaaaaaaaaaaaaa',
      deliveryBriefVersion: 'aaaaaaaaaaaaaaaa',
      reviewBriefVersion: 'aaaaaaaaaaaaaaaa',
      requiredAspect: 'requirement-fit',
      contentStatus: 'concerns',
      contentSummary: 'The 830-card first-paint target remains unmet and lacks idle-host confirmation',
      evidenceChecked: null,
      missing: null,
      ranAspects: ['build-tests', 'requirement-fit'],
      skippedAspects: [],
    };

    async function mountGap(review: NonNullable<TaskDeliveryClaimAnswer['contentReview']>) {
      const tasks = {
        moveJob: vi.fn(() => of({})),
        reviewContentAgainstCurrentBrief: vi.fn(() => of({ reviewAttemptId: 'r-2', briefVersion: 'aaaa' })),
        refresh: vi.fn(),
      };
      const fixture = await mount(job({ state: TaskState.HumanReview }),
        vi.fn(() => of(answer({ contentReview: review }))), tasks);
      const host = fixture.nativeElement as HTMLElement;
      const click = async (testId: string) => {
        host.querySelector<HTMLButtonElement>(`[data-testid="${testId}"]`)!.click();
        fixture.detectChanges();
        await fixture.whenStable();
      };
      return { fixture, host, click, tasks };
    }

    it('offers Review again and Accept with a reason for concerns on the current brief', async () => {
      const { fixture, host, click, tasks } = await mountGap(concerns);
      const section = host.querySelector('[data-testid="content-verdict-acceptance"]');
      expect(section?.textContent).toContain('returned concerns');
      expect(section?.textContent).toContain('The 830-card first-paint target remains unmet');
      expect(host.querySelector('[data-testid="stale-brief-acceptance"]')).toBeNull();

      await click('review-current-brief');
      expect(tasks.reviewContentAgainstCurrentBrief).toHaveBeenCalledWith('task', '/workspace/tasks');

      await click('accept-with-reason-open');
      const input = host.querySelector<HTMLTextAreaElement>('[data-testid="accept-with-reason-input"]')!;
      input.value = 'Owner accepts; first paint moves to a follow-up.';
      input.dispatchEvent(new Event('input'));
      fixture.detectChanges();
      await click('accept-with-reason-submit');
      expect(tasks.moveJob).toHaveBeenCalledWith('task', TaskState.Completed, '/workspace/tasks', undefined,
        'Owner accepts; first paint moves to a follow-up.', true);
    });

    it('offers the actions for block, and none for a pass', async () => {
      const blocked = await mountGap({ ...concerns, contentStatus: 'block' });
      expect(blocked.host.querySelector('[data-testid="content-verdict-acceptance"]')).not.toBeNull();
      const passed = await mountGap({ ...concerns, contentStatus: 'pass' });
      expect(passed.host.querySelector('[data-testid="content-verdict-acceptance"]')).toBeNull();
      expect(passed.host.querySelector('[data-testid="accept-with-reason-open"]')).toBeNull();
    });

    it('keeps the changed-brief section unchanged when the brief moved', async () => {
      const { host } = await mountGap({ ...concerns, currentBriefVersion: 'bbbbbbbbbbbbbbbb' });
      expect(host.querySelector('[data-testid="stale-brief-acceptance"]')?.textContent)
        .toContain('Acceptance is blocked by a changed brief');
      expect(host.querySelector('[data-testid="brief-difference-link"]')).not.toBeNull();
      expect(host.querySelector('[data-testid="review-current-brief"]')?.textContent)
        .toContain('Review again against the current brief');
      expect(host.querySelector('[data-testid="accept-with-reason-open"]')).not.toBeNull();
      expect(host.querySelector('[data-testid="content-verdict-acceptance"]')).toBeNull();
    });
  });
  /**
   * AGT-3867, F10: DEMO-19's content review returned concerns on the current
   * brief. The detail offered steps only for a changed brief, so the owner had
   * none; the round that closes the gap existed only on the board card.
   */
  it('offers the board next steps for a concern on the current brief', async () => {
    const review = { ...conceptReview, mode: 'coding' as const, requiredAspect: 'requirement-fit',
      contentStatus: 'concerns', currentBriefVersion: '028a09dc0b85', deliveryBriefVersion: '028a09dc0b85',
      reviewBriefVersion: '028a09dc0b85' };
    const fixture = await mount(job({ state: TaskState.HumanReview }),
      vi.fn(() => of(answer({ contentReview: review }))));
    TestBed.inject(HttpTestingController).expectOne('/api/tasks/acceptance-readiness').flush({
      checkedAtUtc: '2026-11-15T10:00:00Z', counts: {},
      items: [{ ...readinessItem('task', 'review-concerns'), taskKey: 'agt::task' }],
    });
    await fixture.whenStable();
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="stale-brief-acceptance"]')).toBeNull();
    expect(host.querySelector('[data-testid="acceptance-next-steps-reason"]')?.textContent)
      .toContain('The requirement-fit review returned concerns');
    expect(host.querySelector('[data-testid="acceptance-next-steps-send-round"]')).not.toBeNull();
    expect(host.querySelector('[data-testid="review-current-brief"]')).not.toBeNull();
    expect(host.querySelector('[data-testid="accept-with-reason-open"]')).not.toBeNull();
  });
  describe('AGT-3812 build-tests on the acceptance grounds', () => {
    const passingContent: NonNullable<TaskDeliveryClaimAnswer['contentReview']> = {
      mode: 'coding',
      currentBriefVersion: 'aaaaaaaaaaaaaaaa',
      deliveryBriefVersion: 'aaaaaaaaaaaaaaaa',
      reviewBriefVersion: 'aaaaaaaaaaaaaaaa',
      requiredAspect: 'requirement-fit',
      contentStatus: 'pass',
      contentSummary: 'Demo change verified.',
      evidenceChecked: null,
      missing: null,
      ranAspects: ['build-tests', 'requirement-fit'],
      skippedAspects: [],
    };

    function withBuildTests(result: string, reason: string, outputRef: string | null = null): Partial<TaskInfo> {
      return {
        state: TaskState.HumanReview,
        testEvidence: {
          runId: null, runCommit: null, runState: null, runResult: null,
          matchQuality: 'perfect', direction: 'exact', distance: 0, diffContained: true,
          evidenceState: 'not-proven', awaitingEvidence: false,
          summary: 'Review build-tests',
          sources: [{
            kind: 'review-build-tests', id: 'review-1', commit: '5301026033f4', result,
            observedAt: '2026-11-15T22:00:43Z', summary: 'Review build-tests', reason,
            reportRef: 'remote-review-grade-review-1.md', outputRef,
          }],
        } as TaskInfo['testEvidence'],
      };
    }

    it('is green when content and build-tests both pass', async () => {
      const fixture = await mount(
        job(withBuildTests('passed', 'verify-1 and verify-2 passed.')),
        vi.fn(() => of(answer({ contentReview: passingContent }))));
      const host = fixture.nativeElement as HTMLElement;

      expect(host.querySelector('[data-testid="completion-content-review"]')?.getAttribute('data-state')).toBe('pass');
      expect(host.querySelector('[data-testid="completion-build-tests-gap"]')).toBeNull();
    });

    it('is not green while the integration branch is red, and states the branch with a link to the output', async () => {
      const sentence = 'npm test (verify-2) is red on develop at 08eb16b7 without this change; failing tests: test.';
      const fixture = await mount(
        job(withBuildTests('integration-red', sentence, 'remote-review-review-1-baseline_verify-2_stdout_log')),
        vi.fn(() => of(answer({ contentReview: passingContent }))));
      const host = fixture.nativeElement as HTMLElement;

      expect(host.querySelector('[data-testid="completion-content-review"]')?.getAttribute('data-state')).toBe('gap');
      expect(host.querySelector('[data-testid="completion-build-tests-gap"]')?.textContent).toContain(sentence);
      expect(host.querySelector('[data-testid="completion-build-tests-output"]')?.getAttribute('href'))
        .toBe('/api/tasks/task/files/remote-review-review-1-baseline_verify-2_stdout_log?watchPath=%2Fworkspace%2Ftasks&scope=workspace');
    });

    it('is not green while the build-tests verdict is missing', async () => {
      const fixture = await mount(
        job(withBuildTests('not-proven', 'Build-tests verdict is missing for verify-1.')),
        vi.fn(() => of(answer({ contentReview: passingContent }))));
      const host = fixture.nativeElement as HTMLElement;

      expect(host.querySelector('[data-testid="completion-content-review"]')?.getAttribute('data-state')).toBe('gap');
      expect(host.querySelector('[data-testid="completion-build-tests-gap"]')?.textContent)
        .toContain('Build-tests verdict is missing for verify-1.');
      expect(host.querySelector('[data-testid="completion-build-tests-report"]')).not.toBeNull();
    });
  });

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
          actor: 'human:operator',
          deliveryBriefVersion: 'aaaaaaaaaaaaaaaa',
          currentBriefVersion: 'bbbbbbbbbbbbbbbb',
          reviewBriefVersion: 'aaaaaaaaaaaaaaaa',
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
    expect(host.querySelector('[data-testid="delivery-claim-override-audit"]')?.textContent)
      .toContain('By human:operator · Delivery brief aaaaaaaaaaaa · Current brief bbbbbbbbbbbb');
    expect(host.querySelector('[data-testid="delivery-claim-panel"]')?.getAttribute('data-integration'))
      .toBe('not-integrated');
    expect(host.querySelector('[data-testid="delivery-claim-panel"]')?.getAttribute('data-settled'))
      .toBe('true');
  });

  it('says who accepted the card when the system accepted it automatically (AGT-3621)', async () => {
    const claim = {
      basis: 'integrated-delivery' as const,
      evidence: 'Contained in develop.',
      actor: 'system',
      recordedAt: '2026-11-09T10:00:00Z',
    };
    const automatic = await mount(job(), vi.fn(() => of(answer({ completionClaim: {
      ...claim, acceptedAutomaticallyBy: 'the acceptance rail (project autonomy Balanced)',
    } }))));
    expect((automatic.nativeElement as HTMLElement)
      .querySelector('[data-testid="delivery-claim-automatic-acceptance"]')?.textContent)
      .toContain('Accepted automatically by the acceptance rail (project autonomy Balanced).');

    const person = await mount(job(), vi.fn(() => of(answer({ completionClaim: { ...claim, actor: 'human:operator' } }))));
    expect((person.nativeElement as HTMLElement)
      .querySelector('[data-testid="delivery-claim-automatic-acceptance"]')).toBeNull();
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

  /**
   * AGT-3076: AGT-3042 was integrated, but the rebuilt authority store lost
   * its run attempt. The delivery panel says why acceptance needs an override.
   */
  it('explains that acceptance needs an override when the run attempt is gone', async () => {
    const fixture = await mount(job({
      state: TaskState.HumanReview,
      acceptanceAttemptGap: {
        authorityEpoch: 2,
        subjectRunAttemptId: 'run-3042',
        resultSha: 'abcdef0123456789abcdef0123456789abcdef01',
        reason: "Acceptance refused: no run attempt in the current authority epoch (2) for 'AGT-3042'.",
      },
    }), vi.fn(() => throwError(() => new Error('offline'))));
    const host = fixture.nativeElement as HTMLElement;

    const notice = host.querySelector('[data-testid="acceptance-attempt-gap"]');
    expect(notice?.textContent).toContain('Acceptance needs an operator override');
    expect(notice?.textContent).toContain('no run attempt in the current authority epoch (2)');
  });

  it('renders nothing before a card reaches a delivered lane', async () => {
    const fixture = await mount(job({ state: TaskState.Progress }));

    expect((fixture.nativeElement as HTMLElement)
      .querySelector('[data-testid="delivery-claim-panel"]')).toBeNull();
  });
});
