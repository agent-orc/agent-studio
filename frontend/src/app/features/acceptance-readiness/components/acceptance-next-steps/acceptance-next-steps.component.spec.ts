import { describe, expect, it, vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { of } from 'rxjs';
import { AcceptanceNextStepsComponent } from './acceptance-next-steps.component';
import { AcceptanceReadinessStore, type AcceptanceRoundDraft } from '../../state/acceptance-readiness.store';
import { TaskService } from '../../../../services/task.service';
import { NotificationService } from '@studio/ui/services/notification.service';
import type { TaskDeliveryClaimAnswer } from '../../../../models/task.model';
import type { AcceptanceReadinessItem } from '../../models/acceptance-readiness.model';
import { readinessItem, reviewCard } from '../../testing/acceptance-readiness.fixtures';

type Review = NonNullable<TaskDeliveryClaimAnswer['contentReview']>;

/** DEMO-19 in the playthrough: concerns on the current brief, both versions 028a09dc0b85. */
const currentBriefConcerns: Review = {
  mode: 'coding',
  currentBriefVersion: '028a09dc0b85aaaa',
  deliveryBriefVersion: '028a09dc0b85aaaa',
  reviewBriefVersion: '028a09dc0b85aaaa',
  requiredAspect: 'requirement-fit',
  contentStatus: 'concerns',
  contentSummary: 'The lane hides the reason.',
  evidenceChecked: null,
  missing: null,
  ranAspects: ['build-tests', 'requirement-fit'],
  skippedAspects: [],
};

async function mount(item: AcceptanceReadinessItem | null, review: Review | null = currentBriefConcerns,
  checkCard?: () => Promise<void>) {
  const answer = signal(item);
  const draft = signal<AcceptanceRoundDraft | null>(null);
  const store = {
    itemFor: vi.fn(() => answer()),
    checkCard: checkCard ?? vi.fn(async () => { if (answer()) answer.set({ ...answer()! }); }),
    draftFor: vi.fn((origin: string) => draft()?.origin === origin ? draft() : null),
    openRound: vi.fn((job, origin) => draft.set({ job, item: item!, origin })),
    roundError: signal(null),
    sendingRound: signal(false),
    closeRound: vi.fn(() => draft.set(null)),
    markAddressed: vi.fn(),
  };
  const tasks = {
    moveJob: vi.fn(() => of({})),
    reviewContentAgainstCurrentBrief: vi.fn(() => of({ reviewAttemptId: 'r', briefVersion: 'b' })),
    getBriefDifference: vi.fn(),
    refresh: vi.fn(),
  };
  TestBed.resetTestingModule();
  await TestBed.configureTestingModule({
    imports: [AcceptanceNextStepsComponent],
    providers: [
      provideZonelessChangeDetection(),
      { provide: AcceptanceReadinessStore, useValue: store },
      { provide: TaskService, useValue: tasks },
      { provide: NotificationService, useValue: { success: vi.fn() } },
    ],
  }).compileComponents();
  const fixture = TestBed.createComponent(AcceptanceNextStepsComponent);
  fixture.componentRef.setInput('job', reviewCard('DEMO-19'));
  fixture.componentRef.setInput('contentReview', review);
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement, store, tasks, answer };
}

const testId = (host: HTMLElement, id: string) => host.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

describe('AcceptanceNextStepsComponent (AGT-3867)', () => {
  it('offers the board steps for a concern on the current brief, where the detail offered none', async () => {
    const { host, store } = await mount(readinessItem('DEMO-19', 'review-concerns'));
    expect(store.checkCard).toHaveBeenCalledOnce();
    expect(testId(host, 'acceptance-next-steps')?.getAttribute('data-readiness')).toBe('review-concerns');
    expect(testId(host, 'acceptance-next-steps-reason')?.textContent)
      .toContain('Accept will be refused. The requirement-fit review returned concerns: The lane hides the reason.');
    expect(testId(host, 'stale-brief-acceptance')).toBeNull();
    expect(testId(host, 'acceptance-next-steps-send-round')).not.toBeNull();
    expect(testId(host, 'review-current-brief')).not.toBeNull();
    expect(testId(host, 'accept-with-reason-open')).not.toBeNull();
    expect(testId(host, 'brief-difference-link')).toBeNull();
  });

  it('shows the conflicting accept decision and current review source in the detail', async () => {
    const item = readinessItem('DEMO-19', 'review-concerns', {
      reviewAttemptId: 'review-current', reviewedAtUtc: '2026-11-26T10:00:00Z',
    });
    const { fixture, host } = await mount(item);
    fixture.componentRef.setInput('job', reviewCard('DEMO-19', { orchestratorVerdict: 'accept' }));
    fixture.detectChanges();
    expect(testId(host, 'acceptance-verdict-conflict')?.textContent)
      .toContain('Review decision: accept; requirement-fit: concerns');
    expect(testId(host, 'acceptance-review-source')?.textContent).toContain('review-current');
    expect(testId(host, 'acceptance-review-source')?.textContent).toContain('2026-11-26');
  });

  it('keeps claim concern provenance visible before readiness answers', async () => {
    const { host } = await mount(null, { ...currentBriefConcerns,
      reviewAttemptId: 'review-from-claim', reviewedAtUtc: '2026-11-26T10:00:00Z' });
    expect(testId(host, 'acceptance-review-source')?.textContent).toContain('review-from-claim');
    expect(testId(host, 'content-verdict-acceptance')?.textContent).toContain('The lane hides the reason.');
  });

  it('clears old claim concerns when a newer readiness pass arrives, including a late old claim response', async () => {
    const old = { ...currentBriefConcerns, runAttemptId: 'run-current', reviewAttemptId: 'review-old',
      reportRef: 'reports/old.md', reviewedAtUtc: '2026-11-26T10:00:00Z' };
    const { fixture, host, answer } = await mount(null, old);
    answer.set(readinessItem('DEMO-19', 'ready', { runAttemptId: 'run-current',
      reviewAttemptId: 'review-new', reportRef: 'reports/new.md', reviewedAtUtc: '2026-11-26T11:00:00Z' }));
    fixture.detectChanges();
    expect(testId(host, 'acceptance-next-steps')).toBeNull();
    fixture.componentRef.setInput('contentReview', { ...old });
    fixture.detectChanges();
    expect(testId(host, 'acceptance-next-steps')).toBeNull();
  });

  it.each(['ready', 'review-concerns'] as const)('keeps current %s evidence over a later-arriving older-run claim', async readiness => {
    const { fixture, host } = await mount(readinessItem('DEMO-19', readiness, {
      runAttemptId: 'run-current', reviewAttemptId: 'review-current', reviewedAtUtc: '2026-11-26T10:00:00Z',
    }), { ...currentBriefConcerns, contentStatus: readiness === 'ready' ? 'concerns' : 'pass',
      runAttemptId: 'run-old', reviewAttemptId: 'review-old',
      reviewedAtUtc: '2026-11-26T12:00:00Z', currentBriefVersion: 'different-brief' });
    fixture.detectChanges();
    if (readiness === 'ready') expect(testId(host, 'acceptance-next-steps')).toBeNull();
    else {
      expect(testId(host, 'acceptance-review-source')?.textContent).toContain('review-current');
      expect(testId(host, 'acceptance-secondary-gaps')?.textContent ?? '').not.toContain('brief changed');
    }
  });

  it('rejects readiness from a superseded delivery run even when its review date is newer', async () => {
    const { fixture, host } = await mount(readinessItem('DEMO-19', 'ready', {
      runAttemptId: 'run-old', reviewedAtUtc: '2026-11-26T12:00:00Z',
    }), { ...currentBriefConcerns, runAttemptId: 'run-current', reviewAttemptId: 'review-current' });
    fixture.componentRef.setInput('job', reviewCard('DEMO-19', {
      commit: { sha: 'current-sha', shortSha: 'current', message: 'Current delivery', filesChanged: 0,
        files: [], at: '2026-11-26T09:00:00Z', runAttemptId: 'run-current' },
    }));
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(testId(host, 'acceptance-readiness-fallback')?.textContent).toContain('not been verified');
    expect(testId(host, 'acceptance-review-source')?.textContent).toContain('review-current');
  });

  it('keeps a ready answer without optional provenance from falling back to legacy claim concerns', async () => {
    const { host } = await mount(readinessItem('DEMO-19', 'ready'), {
      ...currentBriefConcerns, deliveryBriefVersion: null, reviewBriefVersion: null,
    });
    expect(testId(host, 'acceptance-next-steps')).toBeNull();
  });

  it('does not treat a failed recheck retaining a cached pass as a fresh verified pass', async () => {
    const { host } = await mount(readinessItem('DEMO-19', 'ready'), currentBriefConcerns,
      vi.fn(async () => undefined));
    expect(testId(host, 'acceptance-readiness-fallback')?.textContent).toContain('unavailable');
    expect(testId(host, 'content-verdict-acceptance')).toBeNull();
    expect(host.textContent).not.toContain('Accept will be refused');
    expect(testId(host, 'review-current-brief')).toBeNull();
  });

  it('rejects a readiness answer for a different delivery ref when no run identity was recorded', async () => {
    const { fixture, host } = await mount(readinessItem('DEMO-19', 'ready', {
      latestResultRef: 'refs/heads/task/old-delivery',
    }));
    fixture.componentRef.setInput('job', reviewCard('DEMO-19', {
      commit: { sha: 'current-sha', shortSha: 'current', message: 'Current delivery', filesChanged: 0,
        files: [], at: '2026-11-26T09:00:00Z', branch: 'task/current-delivery' },
    }));
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(testId(host, 'acceptance-readiness-fallback')?.textContent).toContain('not been verified');
    expect(host.textContent).not.toContain('Accept will be refused');
  });

  it('does not use an earlier same-run pass over a later claim review', async () => {
    const { host } = await mount(readinessItem('DEMO-19', 'ready', {
      runAttemptId: 'run-current', reviewAttemptId: 'review-old', reportRef: 'reports/current.md',
      reviewedAtUtc: '2026-11-26T10:00:00Z',
    }), { ...currentBriefConcerns, runAttemptId: 'run-current', reviewAttemptId: 'review-new',
      reportRef: 'reports/current.md', reviewedAtUtc: '2026-11-26T11:00:00Z' });
    expect(testId(host, 'acceptance-review-source')?.textContent).toContain('review-new');
    expect(testId(host, 'acceptance-readiness-fallback')?.textContent).toContain('latest review');
    expect(host.textContent).not.toContain('Accept will be refused');
  });

  it('distinguishes checking and unavailable claim fallback from a verified refusal', async () => {
    let finish!: () => void;
    const pending = new Promise<void>(resolve => { finish = resolve; });
    const { fixture, host, answer } = await mount(null, currentBriefConcerns, vi.fn(() => pending));
    expect(testId(host, 'acceptance-readiness-fallback')?.textContent).toContain('Checking acceptance readiness');
    expect(host.textContent).not.toContain('Accept will be refused');
    expect(testId(host, 'review-current-brief')).toBeNull();
    expect(testId(host, 'accept-with-reason-open')).toBeNull();
    finish();
    await fixture.whenStable();
    fixture.detectChanges();
    fixture.detectChanges();
    expect(testId(host, 'acceptance-readiness-fallback')?.textContent).toContain('unavailable');
    expect(host.textContent).not.toContain('Accept will be refused');
    answer.set(readinessItem('DEMO-19', 'ready'));
    fixture.detectChanges();
    expect(testId(host, 'acceptance-next-steps')).toBeNull();
  });

  it('marks the gap addressed once the content review is requested, so a recorded refusal drops (F2)', async () => {
    const { fixture, host, store, tasks } = await mount(readinessItem('DEMO-19', 'review-concerns'));
    testId(host, 'review-current-brief')!.click();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(tasks.reviewContentAgainstCurrentBrief).toHaveBeenCalledOnce();
    expect(store.markAddressed).toHaveBeenCalledWith(expect.objectContaining({ id: 'DEMO-19' }));
  });

  it('opens the prefilled round in the detail', async () => {
    const { fixture, host, store } = await mount(readinessItem('DEMO-19', 'review-concerns'));
    testId(host, 'acceptance-next-steps-send-round')!.click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(store.openRound).toHaveBeenCalledWith(expect.objectContaining({ id: 'DEMO-19' }), 'detail');
    const prompt = document.querySelector('[data-testid="acceptance-round-prompt"]') as HTMLTextAreaElement | null;
    expect(prompt?.value).toBe('Close the acceptance gap on this card.');
  });

  it('accepts with a written reason as an operator override', async () => {
    const { fixture, host, tasks } = await mount(readinessItem('DEMO-19', 'review-concerns'));
    testId(host, 'accept-with-reason-open')!.click();
    fixture.detectChanges();
    const input = testId(host, 'accept-with-reason-input') as HTMLTextAreaElement;
    input.value = 'The concern is a known follow-up.';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    testId(host, 'accept-with-reason-submit')!.click();
    expect(tasks.moveJob).toHaveBeenCalledWith('DEMO-19', '6-completed', '/tmp/watch', undefined,
      'The concern is a known follow-up.', true);
  });

  it('offers no override where the policy cannot admit one', async () => {
    const { host } = await mount(readinessItem('DEMO-19', 'blocked', {
      headline: 'Completion refused',
      overrideAllowed: false,
      refusal: 'Completion refused: the required report is missing (results/report.md).',
    }));
    expect(testId(host, 'acceptance-next-steps-send-round')).not.toBeNull();
    expect(testId(host, 'accept-with-reason-open')).toBeNull();
    expect(testId(host, 'acceptance-primary-next-step')?.textContent).not.toContain('override');
  });

  it('offers a round but no override for a batch-gate refusal', async () => {
    const { host } = await mount(readinessItem('DEMO-19', 'integrated-unverified', {
      stage: 'batch-gate', overrideAllowed: false, refusal: 'batch-gate-evidence-missing',
    }), null);
    expect(testId(host, 'acceptance-next-steps-send-round')).not.toBeNull();
    expect(testId(host, 'accept-with-reason-open')).toBeNull();
    expect(testId(host, 'acceptance-primary-next-step')?.textContent)
      .toContain('Send a round to close the batch gate evidence gap');
    expect(testId(host, 'acceptance-primary-next-step')?.textContent).not.toContain('override');
  });

  it('keeps the brief-change steps for a stale verdict, with the round when one is offered', async () => {
    const stale = { ...currentBriefConcerns, currentBriefVersion: 'bbbbbbbbbbbbbbbb' };
    const { host } = await mount(readinessItem('DEMO-19', 'verdict-stale'), stale);
    expect(testId(host, 'stale-brief-acceptance')?.textContent).toContain('Acceptance is blocked by a changed brief');
    expect(testId(host, 'brief-difference-link')).not.toBeNull();
    expect(testId(host, 'acceptance-next-steps-send-round')).not.toBeNull();
  });

  it('says no brief version was recorded for a missing delivery brief, not that it changed (F12)', async () => {
    const { host } = await mount(null, { ...currentBriefConcerns, contentStatus: null, deliveryBriefVersion: null });
    const gap = testId(host, 'stale-brief-acceptance');
    expect(gap?.getAttribute('data-brief-gap')).toBe('unrecorded');
    expect(gap?.textContent).toContain('No brief version was recorded for this delivery');
    expect(gap?.textContent).not.toContain('changed brief');
    expect(testId(host, 'brief-difference-link')).toBeNull();
    expect(testId(host, 'review-current-brief')).toBeNull();
    expect(testId(host, 'acceptance-readiness-fallback')?.textContent).toContain('unavailable');
  });

  it('leads with the readiness reason and its next step and lists the brief gap and other gaps under it (F12)', async () => {
    const { fixture, host } = await mount(readinessItem('DEMO-19', 'no-content-verdict', { headline: 'No content verdict' }),
      { ...currentBriefConcerns, contentStatus: null, deliveryBriefVersion: null });
    fixture.componentRef.setInput('otherGaps', ['No delivery branch or commit was found for this card.']);
    fixture.detectChanges();
    expect(testId(host, 'acceptance-next-steps-status')?.textContent).toContain('No content verdict');
    expect(testId(host, 'acceptance-primary-next-step')?.textContent).toContain('Next step: Run the requirement-fit review for the current run.');
    expect(testId(host, 'stale-brief-acceptance')).toBeNull();
    expect([...host.querySelectorAll('[data-testid="acceptance-secondary-gaps"] li')].map(li => li.textContent?.trim()))
      .toEqual(['No delivery branch or commit was found for this card.', 'No brief version was recorded for this delivery.']);
  });

  it('renders nothing for a ready card on the current brief', async () => {
    const { host } = await mount(readinessItem('DEMO-19', 'ready'), { ...currentBriefConcerns, contentStatus: 'pass' });
    expect(testId(host, 'acceptance-next-steps')).toBeNull();
  });
});
