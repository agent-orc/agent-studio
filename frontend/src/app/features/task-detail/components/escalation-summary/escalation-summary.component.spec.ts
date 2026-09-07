import { beforeEach, describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { of, throwError } from 'rxjs';
import { EscalationSummaryComponent } from './escalation-summary.component';
import { TaskService } from '../../../../services/task.service';
import { TaskTimelinePollService } from '../../../polling/services/task-timeline-poll.service';
import type { TaskDetail } from '../../../../models/task.model';
import type { TaskTimelineEvent } from '../../../task-timeline';

/** localStorage key the component persists per-task collapse under. */
const COLLAPSE_KEY = 'taskboard.escalation.collapsed';

function detail(over: { id?: string; state?: string; orchestratorVerdict?: string } = {}): TaskDetail {
  return {
    info: {
      id: over.id ?? 'AGT-1994',
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
    },
    reviewEvidence: [],
  } as unknown as TaskDetail;
}

function mount(opts: {
  followUp?: string | null;
  reviews?: unknown[];
  events?: TaskTimelineEvent[];
  detail?: TaskDetail;
}) {
  const taskStub = {
    listCodeReviews: () => of({ entries: opts.reviews ?? [] }),
    readJobFile: () =>
      opts.followUp == null ? throwError(() => ({ status: 404 })) : of(opts.followUp),
  };
  const timelineStub = { events: signal<TaskTimelineEvent[]>(opts.events ?? []) };

  TestBed.configureTestingModule({
    imports: [EscalationSummaryComponent],
    providers: [
      provideZonelessChangeDetection(),
      { provide: TaskService, useValue: taskStub },
      { provide: TaskTimelinePollService, useValue: timelineStub },
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
            disposition: 'Escalate',
            summary: 'Escalate two open findings; loop budget exhausted.',
            assessments: [
              { finding: 'Frontend verification missing.', action: 'Escalate', reason: 'Budget exhausted.' },
              { finding: 'Live probe missing.', action: 'Escalate', reason: 'Budget exhausted.' },
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
    });
    const el: HTMLElement = fixture.nativeElement;

    expect(el.querySelector('[data-testid="escalation-essence"]')?.textContent).toContain(
      '1 review round · Grade B · 2 open findings · Reissue budget exhausted',
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
      '0 review rounds · Grade not recorded · 1 open finding · Completion gate',
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

  it('places the three decisions in the panel and emits the existing triage action', () => {
    const fixture = mount({ reviews: [] });
    const el: HTMLElement = fixture.nativeElement;
    const emitted: unknown[] = [];
    fixture.componentInstance.triageAction.subscribe((action) => emitted.push(action));

    expect(el.querySelector('[data-testid="escalation-action-reissue-escalated"]')?.textContent?.trim()).toBe('Continue (reissue)');
    expect(el.querySelector('[data-testid="escalation-action-accept-escalated"]')?.textContent?.trim()).toBe('Accept as-is');
    expect(el.querySelector('[data-testid="escalation-action-discard-escalated"]')?.textContent?.trim()).toBe('Abort');

    (el.querySelector('[data-testid="escalation-action-accept-escalated"]') as HTMLButtonElement).click();
    expect(emitted).toEqual([{
      id: 'accept-escalated',
      label: 'Accept as-is',
      intent: { kind: 'move', targetState: '6-completed' },
    }]);
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
    expect(el.querySelector('[data-testid="escalation-essence"]')?.textContent).toContain('0 open findings');
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
    }).nativeElement;
    expect(el.querySelector('[data-testid="escalation-essence"]')?.textContent).toContain(
      '1 review round · Grade B · 0 open findings',
    );
    expect(el.querySelectorAll('[data-testid="escalation-essence-merge"]')).toHaveLength(1);
  });
});
