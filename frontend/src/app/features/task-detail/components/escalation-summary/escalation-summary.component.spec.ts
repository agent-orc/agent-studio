import { beforeEach, describe, expect, it, vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { HttpClient, HttpParams } from '@angular/common/http';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { of, throwError, Subject } from 'rxjs';
import { EscalationSummaryComponent } from './escalation-summary.component';
import { TaskService } from '../../../../services/task.service';
import { TaskTimelinePollService } from '../../../polling/services/task-timeline-poll.service';
import { RunTimelinePollService } from '../../../polling/services/run-timeline-poll.service';
import type { TaskDetail, ReviewProjectionView } from '../../../../models/task.model';
import type { TaskTimelineEvent } from '../../../task-timeline';

/** localStorage key the component persists per-task collapse under. */
const COLLAPSE_KEY = 'taskboard.escalation.collapsed';

/** A single local `code-review-grade-*.md` round, matching the fixtures below that pass one graded review. */
function oneLocalRound(grade: string): ReviewProjectionView {
  return {
    attempts: [{
      plane: 'local',
      attemptId: 'code-review-grade-2026-07-09T19-22-02Z',
      receivedAt: '2026-07-09T19:22:02Z',
      outcome: 'pass',
      grade,
      buildTestsResult: 'not-proven',
      buildTestsReason: null,
      aspects: [],
      subjectSha: null,
      reportRef: 'code-review-grade-2026-07-09T19-22-02Z.md',
    }],
    rounds: 1,
    latestPlane: 'local',
    latestOutcome: 'pass',
    latestReceivedAt: '2026-07-09T19:22:02Z',
    blockingAspects: [],
    delivery: { status: 'integrated', reason: null },
    decisionRequired: { required: false, source: null, reason: null },
  };
}

function detail(over: {
  id?: string;
  state?: string;
  orchestratorVerdict?: string;
  reviewProjection?: ReviewProjectionView | null;
  needsInput?: TaskDetail['info']['needsInput'];
  pendingIntent?: TaskDetail['info']['pendingIntent'];
} = {}): TaskDetail {
  return {
    info: {
      id: over.id ?? 'AGT-1994',
      projectName: 'Agent Studio',
      watchPath: '/ws',
      state: over.state ?? '5e-escalated',
      orchestratorVerdict: over.orchestratorVerdict ?? 'escalate',
      commits: [
        { sha: 'b2ed3f47', shortSha: 'b2ed3f47', message: 'm', filesChanged: 4, files: ['a', 'b', 'c', 'd'], at: '' },
      ],
      mergeSignal: {
        branch: 'task/AGT-1994',
        inIntegration: true,
        inRelease: true,
        integrationBranch: 'develop',
        releaseBranch: 'main',
        integrationSha: 'b2ed3f4',
        releaseSha: '1a526e9',
      },
      reviewProjection: over.reviewProjection ?? null,
      needsInput: over.needsInput ?? null,
      pendingIntent: over.pendingIntent ?? null,
    },
    reviewEvidence: [],
  } as unknown as TaskDetail;
}

function mount(opts: {
  followUp?: string | null;
  reviews?: unknown[];
  reviewsStream?: Subject<{ entries: unknown[] }>;
  events?: TaskTimelineEvent[];
  detail?: TaskDetail;
  continueJob?: ReturnType<typeof vi.fn>;
  retryReview?: ReturnType<typeof vi.fn>;
}) {
  const taskStub = {
    listCodeReviews: () => opts.reviewsStream ?? of({ entries: opts.reviews ?? [] }),
    readJobFile: () =>
      opts.followUp == null ? throwError(() => ({ status: 404 })) : of(opts.followUp),
    continueJob: opts.continueJob ?? vi.fn(() => of({ status: 'queued' })),
  };
  const timelineStub = { events: signal<TaskTimelineEvent[]>(opts.events ?? []) };

  TestBed.configureTestingModule({
    imports: [EscalationSummaryComponent],
    providers: [
      provideZonelessChangeDetection(),
      { provide: TaskService, useValue: taskStub },
      { provide: HttpClient, useValue: { post: opts.retryReview ?? vi.fn(() => of({ status: 'review-started' })) } },
      { provide: TaskTimelinePollService, useValue: timelineStub },
      { provide: RunTimelinePollService, useValue: { runs: signal([]) } },
    ],
  });
  const fixture = TestBed.createComponent(EscalationSummaryComponent);
  fixture.componentRef.setInput('detail', opts.detail ?? detail());
  fixture.detectChanges();
  return fixture;
}

beforeEach(() => {
  try { localStorage.clear(); } catch { /* jsdom always has it, but be defensive */ }
});

describe('EscalationSummaryComponent', () => {
  it('offers Retry review as the primary watchdog recovery action', () => {
    const parked = detail();
    parked.info.parkedBlocker = {
      blockerType: 'environmental', parkedAt: '2026-11-09T01:27:00Z',
      reason: '[environmental] Last review ended as ExecutorRestarted.',
      cause: 'escalated', causeDetail: 'auto-review-stranded',
    } as TaskDetail['info']['parkedBlocker'];
    const retryReview = vi.fn((...request: [string, null, { params: HttpParams }]) => {
      expect(request[2].params.get('watchPath')).toBe('/ws');
      return of({ status: 'review-started' });
    });
    const fixture = mount({ detail: parked, retryReview });
    const root = fixture.nativeElement as HTMLElement;
    const retry = root.querySelector<HTMLButtonElement>('[data-testid="escalation-action-retry-review"]')!;
    expect(retry).toBeTruthy();
    expect(root.querySelector('[data-testid="escalation-action-reissue-escalated"]')?.className)
      .toContain('secondary');
    expect(root.querySelector('[data-testid="escalation-title"]')?.textContent).toContain('Review recovery');
    expect(root.querySelector('[data-testid="escalation-essence"]')?.textContent)
      .not.toContain('Human decision required');
    expect(root.querySelector('[data-testid="escalation-recommendation"]')?.textContent)
      .toContain('Review retry available');
    retry.click();
    fixture.detectChanges();
    expect(retryReview).toHaveBeenCalledWith('/api/tasks/AGT-1994/review/retry', null, expect.anything());
    expect(root.querySelector('[data-testid="review-retry-message"]')?.textContent)
      .toContain('fresh review attempt');
  });

  it('directs a missing server checkout to setup without offering reissue', () => {
    const blocked = detail();
    blocked.info.integration = {
      status: 'pending', integrationBranch: 'main', deliveryRef: null, sha: null,
      detail: 'Delivery could not integrate.', failure: {
        reason: 'This project needs a server checkout before delivery can integrate.',
      },
    } as TaskDetail['info']['integration'];
    const fixture = mount({ detail: blocked });
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="server-checkout-remedy"]')?.textContent)
      .toContain('checkout on the Studio server');
    const links = el.querySelectorAll('[data-testid="server-checkout-remedy"] a');
    expect(links[0]?.getAttribute('href')).toBe('/#/projects/agent-studio/settings');
    expect(links[1]?.getAttribute('href')).toContain('networked-task-server.md#project-checkouts');
    expect(el.querySelector('[data-testid="escalation-action-reissue-escalated"]')).toBeNull();
  });

  it('puts the latest ended attempt in the header and leaves the reason to the park panel', () => {
    const parked = detail();
    parked.info.parkedBlocker = {
      parkedAt: '2026-10-06T04:22:54Z', reason: 'Push protection rejected the branch.',
    } as TaskDetail['info']['parkedBlocker'];
    const fixture = mount({
      detail: parked,
      events: [
        { ts: '2026-10-06T04:21:54Z', kind: 'agent_run_started', actor: 'agent', summary: 'Run started' },
        { ts: '2026-10-06T04:22:53Z', kind: 'agent_run_finished', actor: 'agent', summary: 'Run failed' },
      ],
    });
    const header = (fixture.nativeElement as HTMLElement).querySelector('[data-testid="escalation-essence"]');
    expect(header?.textContent).toContain('Latest attempt ended 2026-10-06 04:22 UTC and parked this card.');
    expect(header?.textContent).not.toContain('Push protection rejected the branch.');
  });

  it('renders the AGT-2736 question and options and submits the answer as a steer', () => {
    const continueJob = vi.fn(() => of({ status: 'queued' }));
    const fixture = mount({
      reviews: [],
      continueJob,
      detail: detail({
        needsInput: {
          message: 'Which deployment strategy?\n\n- A: managed connector (recommended)\n- B: direct LAN',
          firstLine: 'Which deployment strategy?',
          runAttemptId: 'run_4e53d6d6',
          salvageBranch: 'runner/agent-runner-01/AGT-2736',
          artifactPath: 'results/needs-input.md',
        },
      }),
    });
    const root: HTMLElement = fixture.nativeElement;
    expect(root.querySelector('[data-testid="needs-input-message"]')?.textContent).toContain('A: managed connector');
    expect(root.querySelector('[data-testid="needs-input-message"]')?.textContent).toContain('B: direct LAN');

    const answer = root.querySelector('[data-testid="needs-input-answer"]') as HTMLTextAreaElement;
    answer.value = 'Choose A. Keep a LAN fallback for isolated networks.';
    answer.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    (root.querySelector('[data-testid="needs-input-submit"]') as HTMLButtonElement).click();

    expect(continueJob).toHaveBeenCalledWith(
      'AGT-1994',
      'Choose A. Keep a LAN fallback for isolated networks.',
      '/ws',
      undefined,
      undefined,
      undefined,
      'steer',
    );
  });

  it('marks a queued answer that is still the board card head as truncated', () => {
    const needsInput = {
      message: 'Which deployment strategy?', firstLine: 'Which deployment strategy?',
      runAttemptId: 'run_4e53d6d6', salvageBranch: null, artifactPath: null,
    } as unknown as TaskDetail['info']['needsInput'];
    const head = mount({ detail: detail({ needsInput,
      pendingIntent: { mode: 'steer', prompt: 'Choose A', promptTruncated: true } as TaskDetail['info']['pendingIntent'] }) });
    expect((head.nativeElement as HTMLElement).querySelector('[data-testid="needs-input-answer-queued"] span')
      ?.textContent).toBe('Choose A…');
    TestBed.resetTestingModule();

    const full = mount({ detail: detail({ needsInput,
      pendingIntent: { mode: 'steer', prompt: 'Choose A' } as TaskDetail['info']['pendingIntent'] }) });
    expect((full.nativeElement as HTMLElement).querySelector('[data-testid="needs-input-answer-queued"] span')
      ?.textContent).toBe('Choose A');
  });

  it('renders the structured council findings, artifacts, delivery context and recommendation', () => {
    const fixture = mount({
      followUp: '- [ ] Frontend Playwright verification skipped.\n- [ ] Live Haiku probe not run.',
      reviews: [
        {
          fileName: 'code-review-grade-2026-07-09T19-22-02Z.md',
          verdict: 'pass',
          grade: 'B',
          summary: 'Solid first slice, small gaps.',
          model: 'claude-opus-4-8',
          cliType: 'claude',
          runAt: '2026-07-09T19:22:02Z',
          councilReaction: {
            createdAt: '2026-07-09T19:22:03Z',
            reviewFileName: 'code-review-grade-2026-07-09T19-22-02Z.md',
            grade: 'B',
            disposition: 'escalate',
            summary: 'Escalate two open findings; loop budget exhausted.',
            assessments: [
              { finding: 'Frontend verification missing.', action: 'escalate', reason: 'Budget exhausted.' },
              { finding: 'Live probe missing.', action: 'escalate', reason: 'Budget exhausted.' },
            ],
            startsNewRound: false,
            targetJobId: null,
            targetRunAttempt: null,
          },
        },
      ],
      events: [
        {
          ts: '2026-07-09T18:30:00Z', kind: 'quality_loop_reopened', actor: 'quality-loop',
          summary: 'reopened', details: { cause: 'build/test gate failed', reason: 'npm test exit 1' },
        },
        {
          ts: '2026-07-09T19:30:00Z', kind: 'orchestrator_escalated', actor: 'orchestrator',
          summary: 'escalated', details: { attempt: '3', maxAttempts: '3' },
        },
      ],
      detail: detail({ reviewProjection: oneLocalRound('B') }),
    });
    const el: HTMLElement = fixture.nativeElement;

    expect(el.querySelector('[data-testid="escalation-essence"]')?.textContent).toContain(
      '3 review rounds (local) · latest 09.07. 19:22 pass · build and tests not proven · in develop',
    );
    expect(el.querySelector('[data-testid="escalation-essence"]')?.textContent).not.toContain('Frontend Playwright');
    expect(el.querySelector('[data-testid="escalation-grade-documents"]')).not.toBeNull();
    expect(el.querySelector('[data-testid="escalation-council-follow-up"]')?.textContent).toContain(
      'Escalate two open findings',
    );

    // Findings come from the typed council reaction, not the follow-up Markdown.
    const gateItems = el.querySelectorAll('[data-testid="escalation-gate-items"] li');
    expect(gateItems.length).toBe(2);
    expect(el.querySelector('[data-testid="escalation-gate-source"]')?.textContent).toContain('Council reaction');
    expect(el.querySelector('[data-testid="escalation-gate-count"]')?.textContent).toContain('2 open');

    // Delivery context
    expect(el.querySelector('[data-testid="escalation-delivery-counts"]')?.textContent).toContain('1 commit');
    expect(el.querySelector('[data-testid="escalation-delivery-counts"]')?.textContent).toContain('4 files');

    // Recommendation
    const recommendation = el.querySelector('[data-testid="escalation-recommendation"]');
    expect(recommendation?.textContent?.trim()).toBe('Needs decision');
    expect(recommendation?.tagName).toBe('SPAN');
    expect(recommendation?.getAttribute('data-label-kind')).toBe('status');
    expect(recommendation?.getAttribute('aria-label')).toBe('Recommendation: Needs decision');
    expect(el.querySelector('[data-testid="escalation-reissue-1"]')?.textContent).toContain('npm test exit 1');
  });

  it('falls back to the escalate timeline findings when no follow-up file exists', () => {
    const fixture = mount({
      followUp: null,
      reviews: [],
      events: [
        {
          ts: '2026-07-09T19:00:00Z',
          kind: 'orchestrator_escalated',
          actor: 'orchestrator',
          summary: 'escalated',
          details: {
            reason: 'completion gate found unfinished work',
            cause: 'completion-gate',
            findings: JSON.stringify([
              { aspect: 'tests', verdict: 'block', reason: 'missing regression' },
            ]),
          },
        },
      ],
    });
    const el: HTMLElement = fixture.nativeElement;

    expect(el.querySelector('[data-testid="escalation-essence"]')?.textContent).toContain(
      '0 review rounds · Completion gate',
    );
    expect(el.querySelector('[data-testid="escalation-essence"]')?.textContent).not.toContain(
      'completion gate found unfinished work',
    );
    expect(el.querySelector('[data-testid="escalation-gate-source"]')?.textContent).toContain('Completion-gate findings');
    const gateItems = el.querySelectorAll('[data-testid="escalation-gate-items"] li');
    expect(gateItems.length).toBe(1);
    expect(gateItems[0].textContent).toContain('missing regression');
    expect(el.querySelector('[data-testid="escalation-grade-documents"]')).toBeNull();
  });

  it('confirms selected findings and an owner note before emitting a reissue', () => {
    const fixture = mount({ reviews: [], events: [{
      ts: '2026-07-09T19:30:00Z', kind: 'orchestrator_escalated', actor: 'orchestrator',
      summary: 'escalated', details: { findings: JSON.stringify([
        { aspect: 'tests', verdict: 'block', reason: 'Add regression coverage' },
        { aspect: 'quality', verdict: 'block', reason: 'Fix repeated queries' },
      ]) },
    }] });
    const el: HTMLElement = fixture.nativeElement;
    const emitted: unknown[] = [];
    fixture.componentInstance.triageAction.subscribe((action) => emitted.push(action));

    expect(el.querySelector('[data-testid="escalation-action-reissue-escalated"]')?.textContent?.trim()).toBe('Continue (reissue)');
    expect(el.querySelector('[data-testid="escalation-action-accept-escalated"]')?.textContent?.trim()).toBe('Accept with reason');
    expect(el.querySelector('[data-testid="escalation-action-discard-escalated"]')?.textContent?.trim()).toBe('Abort');

    (el.querySelector('[data-testid="escalation-action-reissue-escalated"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(emitted).toEqual([]);
    expect(el.querySelector('[data-testid="escalation-confirm"]')).not.toBeNull();
    const findings = el.querySelectorAll<HTMLInputElement>('.escalation-confirm__findings input');
    expect(findings.length).toBeGreaterThan(0);
    findings[0].click();
    const note = el.querySelector<HTMLTextAreaElement>('[data-testid="escalation-owner-note"]')!;
    note.value = 'Keep the fix bounded';
    note.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="reissue-instructions-preview"]')?.textContent).toContain('Keep the fix bounded');
    (el.querySelector('[data-testid="escalation-confirm-submit"]') as HTMLButtonElement).click();
    expect(emitted).toEqual([
      expect.objectContaining({
        id: 'reissue-escalated', intent: { kind: 'move', targetState: '2-ready' },
        reissueInstructions: expect.objectContaining({ note: 'Keep the fix bounded' }),
      }),
    ]);
    expect((emitted[0] as { reissueInstructions: { findings: string[] } }).reissueInstructions.findings.length)
      .toBe(findings.length - 1);
  });

  it('offers acceptance only with a written override reason', () => {
    const fixture = mount({ reviews: [] });
    const el: HTMLElement = fixture.nativeElement;
    const emitted: unknown[] = [];
    fixture.componentInstance.triageAction.subscribe(action => emitted.push(action));
    fixture.componentInstance.openDecision('accept-escalated');
    fixture.detectChanges();
    const submit = el.querySelector<HTMLButtonElement>('[data-testid="escalation-confirm-submit"]')!;
    expect(submit.disabled).toBe(true);
    const note = el.querySelector<HTMLTextAreaElement>('[data-testid="escalation-owner-note"]')!;
    note.value = 'Owner accepts the documented review gap.';
    note.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    expect(submit.disabled).toBe(false);
    submit.click();
    expect(emitted).toEqual([expect.objectContaining({
      id: 'accept-escalated', operatorOverride: true,
      reason: 'Owner accepts the documented review gap.',
    })]);
  });

  it('waits for review findings before allowing a reissue', () => {
    const reviewsStream = new Subject<{ entries: unknown[] }>();
    const fixture = mount({ reviewsStream, events: [{
      ts: '2026-07-09T19:30:00Z', kind: 'orchestrator_escalated', actor: 'orchestrator',
      summary: 'escalated', details: { findings: JSON.stringify([
        { aspect: 'tests', verdict: 'block', reason: 'Add regression coverage' },
      ]) },
    }] });
    fixture.componentInstance.openDecision('reissue-escalated');
    fixture.detectChanges();
    const el: HTMLElement = fixture.nativeElement;
    const submit = el.querySelector<HTMLButtonElement>('[data-testid="escalation-confirm-submit"]')!;
    expect(submit.disabled).toBe(true);
    expect(el.querySelector('[role="status"]')?.textContent).toContain('Loading review findings');
    reviewsStream.next({ entries: [] });
    fixture.detectChanges();
    expect(submit.disabled).toBe(false);
    expect(el.querySelector<HTMLInputElement>('.escalation-confirm__findings input')?.checked).toBe(true);
  });

  it('replaces three empty context columns with one compact message', () => {
    const base = detail();
    const empty = {
      ...base,
      info: { ...base.info, commits: [], mergeSignal: null },
    } as TaskDetail;
    const el: HTMLElement = mount({ reviews: [], detail: empty }).nativeElement;

    expect(el.querySelector('[data-testid="escalation-context-empty"]')?.textContent?.trim()).toBe(
      'No structured findings, review artifacts, or delivery context were recorded.',
    );
    expect(el.querySelector('[data-testid="escalation-gate-items"]')).toBeNull();
    expect(el.querySelector('[data-testid="escalation-grade-documents"]')).toBeNull();
    expect(el.querySelector('[data-testid="escalation-delivery"]')).toBeNull();
    expect(el.querySelector('[data-testid="escalation-essence"]')?.textContent).toContain('0 review rounds');
  });

  it('renders one readable pending segment when integration and release use main', () => {
    const base = detail();
    const sameTarget = {
      ...base,
      info: {
        ...base.info,
        integration: { status: 'pending', integrationBranch: 'main' },
        mergeSignal: {
          ...base.info.mergeSignal,
          inIntegration: false,
          inRelease: false,
          integrationBranch: 'main',
          releaseBranch: 'main',
          integrationSha: null,
          releaseSha: null,
        },
      },
    } as TaskDetail;
    const el: HTMLElement = mount({ reviews: [], detail: sameTarget }).nativeElement;
    const segments = el.querySelectorAll('[data-testid="escalation-merge-segment"]');

    expect(segments).toHaveLength(1);
    expect(segments[0].textContent?.trim()).toBe('main · pending');
    expect(segments[0].getAttribute('data-state')).toBe('pending');
    expect(el.querySelector('[data-testid="escalation-essence-merge"]')?.getAttribute('aria-label')).toBe(
      'Merge status: not in main',
    );
  });

  it('renders one readable merged segment for a same-target branch', () => {
    const base = detail();
    const sameTarget = {
      ...base,
      info: {
        ...base.info,
        integration: { status: 'integrated', integrationBranch: 'main', sha: 'b2ed3f4' },
        mergeSignal: {
          ...base.info.mergeSignal,
          integrationBranch: 'main',
          releaseBranch: 'main',
        },
      },
    } as TaskDetail;
    const el: HTMLElement = mount({ reviews: [], detail: sameTarget }).nativeElement;
    const segments = el.querySelectorAll('[data-testid="escalation-merge-segment"]');

    expect(segments).toHaveLength(1);
    expect(segments[0].textContent?.trim()).toBe('main ✓ merged');
    expect(segments[0].getAttribute('data-state')).toBe('merged');
  });

  it('renders delivery context per repository and includes the missing reason', () => {
    const base = detail();
    const multiRepository = {
      ...base,
      info: {
        ...base.info,
        integration: {
          status: 'partial', integrationBranch: 'develop', detail: 'runner is incomplete',
          repositories: [
            {
              repository: 'agent-studio',
              commits: [{ sha: 'a', onIntegrationBranch: true, onReleaseBranch: true }],
              integrationBranch: 'develop', releaseBranch: 'main',
              onIntegrationBranch: true, onReleaseBranch: true, detail: '1/1 on develop and main.',
            },
            {
              repository: 'runner',
              commits: [
                { sha: 'b', onIntegrationBranch: true, onReleaseBranch: true },
                { sha: 'c', onIntegrationBranch: false, onReleaseBranch: false },
              ],
              integrationBranch: 'main', releaseBranch: 'main',
              onIntegrationBranch: false, onReleaseBranch: false,
              detail: '1/2 on main; missing: c.',
            },
          ],
        },
      },
    } as TaskDetail;

    const el: HTMLElement = mount({ reviews: [], detail: multiRepository }).nativeElement;
    const repositories = [...el.querySelectorAll('[data-testid="escalation-delivery-repository"]')]
      .map((item) => item.textContent?.trim());

    expect(repositories).toEqual([
      'agent-studio 1/1 develop and main',
      'runner 1/2 main (1/2 on main; missing: c.)',
    ]);
  });
});

describe('EscalationSummaryComponent — collapse (AGT-2060)', () => {
  it('defaults open on an acute 5e-escalated card and shows the detail body', () => {
    const el: HTMLElement = mount({ reviews: [] }).nativeElement;
    expect(el.querySelector('[data-testid="escalation-body"]')).not.toBeNull();
    expect(el.querySelector('[data-testid="escalation-toggle"]')?.getAttribute('aria-expanded')).toBe('true');
  });

  it('defaults closed on any other lane (escalate verdict parked in 5-human-review)', () => {
    const el: HTMLElement = mount({
      reviews: [],
      detail: detail({ id: 'AGT-XREV', state: '5-human-review' }),
    }).nativeElement;
    // Body hidden by default (historical context), but the header essence stays.
    expect(el.querySelector('[data-testid="escalation-body"]')).toBeNull();
    expect(el.querySelector('[data-testid="escalation-toggle"]')?.getAttribute('aria-expanded')).toBe('false');
    expect(el.querySelector('[data-testid="escalation-essence"]')).not.toBeNull();
    expect(el.querySelector('[data-testid="escalation-action-reissue-escalated"]')).toBeNull();
  });

  it('toggles on header click and persists the choice for the task', () => {
    const fixture = mount({ reviews: [] });
    const el: HTMLElement = fixture.nativeElement;
    const toggle = el.querySelector('[data-testid="escalation-toggle"]') as HTMLButtonElement;

    // Opens by default on 5e; clicking the header collapses it.
    toggle.click();
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="escalation-body"]')).toBeNull();
    expect(el.querySelector('[data-testid="escalation-toggle"]')?.getAttribute('aria-expanded')).toBe('false');
    expect(JSON.parse(localStorage.getItem(COLLAPSE_KEY) ?? '{}')).toEqual({ 'AGT-1994': true });

    // Clicking again re-opens and updates the stored preference.
    toggle.click();
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="escalation-body"]')).not.toBeNull();
    expect(JSON.parse(localStorage.getItem(COLLAPSE_KEY) ?? '{}')).toEqual({ 'AGT-1994': false });
  });

  it('honours a stored per-task preference over the lane default', () => {
    // Operator previously collapsed this 5e card — the stored choice must win
    // over the acute-lane "open" default.
    localStorage.setItem(COLLAPSE_KEY, JSON.stringify({ 'AGT-1994': true }));
    const el: HTMLElement = mount({ reviews: [] }).nativeElement;
    expect(el.querySelector('[data-testid="escalation-body"]')).toBeNull();
  });

  it('carries the structured essence without duplicating the merge badge', () => {
    const el: HTMLElement = mount({
      reviews: [
        {
          fileName: 'code-review-grade-x.md',
          verdict: 'pass',
          grade: 'B',
          summary: 's',
          model: 'claude-opus-4-8',
          cliType: 'claude',
          runAt: '2026-07-09T19:22:02Z',
        },
      ],
      detail: detail({ reviewProjection: oneLocalRound('B') }),
    }).nativeElement;
    expect(el.querySelector('[data-testid="escalation-essence"]')?.textContent).toContain(
      '1 review round (local) · latest 09.07. 19:22 pass',
    );
    expect(el.querySelectorAll('[data-testid="escalation-essence-merge"]')).toHaveLength(1);
  });
});
