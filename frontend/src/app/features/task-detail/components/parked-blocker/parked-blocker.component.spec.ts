import { describe, expect, it, beforeEach, vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { of, Subject } from 'rxjs';

import {
  ParkedBlockerComponent,
  resolveParkingRun,
  resolveParkingSessionEvent,
  resolveRunPrompt,
} from './parked-blocker.component';
import { StudioTabStateService } from '../../../studio-shell/services/studio-tab-state.service';
import { NowTickService } from '../../../../services/now-tick.service';
import { TaskService } from '../../../../services/task.service';
import { TaskTimelinePollService } from '../../../polling/services/task-timeline-poll.service';
import type { TaskTimelineEvent } from '../../../task-timeline';
import type { CliOutputLine, ParkedBlockerStatus, TaskDetail, TaskInfo } from '../../../../models/task.model';
import type { RunRecord, RunTimeline } from '../../../../features/run-timeline';
import type { SessionEventsResponse } from '../../../../features/session-events';

/**
 * AGT-2816 acceptance, at component level: AGT-2736 opened after this lands must
 * show when it was parked, that it is an operator decision, the question, the
 * options, and the link to the gap document - without opening a single file.
 */
describe('ParkedBlockerComponent', () => {
  const opened: { relPath: string }[] = [];

  class TabsStub {
    open(tab: { wikiTarget?: { relPath: string } }): void {
      if (tab.wikiTarget) opened.push({ relPath: tab.wikiTarget.relPath });
    }
  }

  const taskService = {
    getRunTimeline: vi.fn(),
    getSessionEvents: vi.fn(),
    getJobOutput: vi.fn(),
  };

  function park(overrides: Partial<ParkedBlockerStatus> = {}): ParkedBlockerStatus {
    return {
      blockerType: 'agent-needs-input',
      conditionKind: 'manual',
      conditionDescription: 'Only a person can clear this park; no automatic precondition is recorded.',
      parkedAt: '2026-09-11T14:44:00.000Z',
      reason: '[agent-needs-input] The remote agent requires operator input: choose-connector-vs-lan',
      recallStatus: 'blocked',
      lastEvaluatedAt: '2026-09-14T11:50:00.000Z',
      detail: 'Only a person can clear this park.',
      lane: '5e-escalated',
      decision: {
        questionId: 'choose-connector-vs-lan-deployment-strategy',
        question: 'Should the Studio backend be reached through the Connector or over the LAN?',
        options: [
          { id: 'a', label: 'Managed connector.', consequences: 'Simpler operations.', recommended: true },
          { id: 'b', label: 'LAN-reachable Studio backend.', consequences: 'Needs network access.', recommended: false },
          { id: 'c', label: 'Ship both behind a setting.', consequences: null, recommended: false },
        ],
        documents: ['docs/operations/setup/docker-compose-connector-gap.md'],
        decisionCardKey: null,
      },
      needsInputFile: 'results/needs-input.md',
      evaluationAgeSeconds: 600,
      evaluationStale: false,
      requiresDecisionCard: true,
      ...overrides,
    };
  }

  const timelineEvents = signal<TaskTimelineEvent[]>([]);

  function info(parkedBlocker: ParkedBlockerStatus | null, id = 'AGT-2736', state = '5e-escalated'): TaskInfo {
    return {
      id,
      key: id,
      projectName: 'PROJ-002',
      watchPath: '/ws',
      state,
      parkedBlocker,
    } as unknown as TaskInfo;
  }

  function detail(parkedBlocker: ParkedBlockerStatus | null, id = 'AGT-2736', state = '5e-escalated'): TaskDetail {
    return {
      info: info(parkedBlocker, id, state),
      promptMarkdown: 'Initial task prompt.',
      promptHistory: [{ index: 1, fileName: 'prompt-1.md', markdown: 'Choose the managed connector.', writtenAt: '2026-09-11T14:40:00Z' }],
      titleHistory: [],
      statusMarkdown: '',
      contextUsage: null,
      log: [],
      summaryState: null,
      reviewEvidence: [],
    };
  }

  function run(overrides: Partial<RunRecord> = {}): RunRecord {
    return {
      index: 2, intent: 'continue', startedAt: '2026-09-11T14:40:00Z', endedAt: '2026-09-11T14:44:00Z',
      status: 'completed', cli: null, model: 'gpt-6-sol', thinkingLevel: 'high', executionLocation: null,
      exitCode: 0, durationSeconds: 240, inputSessionId: null, capturedSessionId: null, resumed: true,
      reason: null, userFollowup: null, lineStart: 1, lineEnd: 2, headShaBefore: null, headShaAfter: null,
      contextRef: null, ...overrides,
    };
  }

  function timeline(runs: RunRecord[] = [run()]): RunTimeline {
    return {
      runCount: runs.length, firstStartedAt: runs[0]?.startedAt ?? null,
      lastActivityAt: runs.at(-1)?.endedAt ?? null, hasActiveRun: false, runs,
      promptEntries: [], refinements: [], runnerEvents: [], reviewAttemptEpoch: 0, reviewAttemptCycles: [],
    };
  }

  function sessionEvents(cli = 'codex'): SessionEventsResponse {
    return {
      events: [{
        ts: '2026-09-11T14:40:00Z', kind: 'continue', cli,
        inputSessionId: 'session-1', capturedSessionId: 'session-1', resumed: true, reason: null,
      }],
      sessionChain: ['session-1'],
      currentSessionId: 'session-1',
    };
  }

  function render(parkedBlocker: ParkedBlockerStatus | null, state = '5e-escalated'): HTMLElement {
    const fixture = TestBed.createComponent(ParkedBlockerComponent);
    fixture.componentRef.setInput('detail', detail(parkedBlocker, 'AGT-2736', state));
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  const text = (el: HTMLElement, testId: string): string =>
    el.querySelector(`[data-testid="${testId}"]`)?.textContent?.trim() ?? '';

  beforeEach(() => {
    opened.length = 0;
    localStorage.clear();
    timelineEvents.set([]);
    taskService.getRunTimeline.mockReset().mockReturnValue(of(timeline()));
    taskService.getSessionEvents.mockReset().mockReturnValue(of(sessionEvents()));
    taskService.getJobOutput.mockReset().mockReturnValue(of([
      { timestamp: '2026-09-11T14:41:00Z', stream: 'stdout', text: 'I need an operator choice.' },
      { timestamp: '2026-09-11T14:42:00Z', stream: 'stdout', text: '[tool] read docs/deployment.md' },
    ]));
    TestBed.configureTestingModule({
      imports: [ParkedBlockerComponent],
      providers: [
        provideZonelessChangeDetection(),
        { provide: StudioTabStateService, useClass: TabsStub },
        { provide: TaskService, useValue: taskService },
        { provide: TaskTimelinePollService, useValue: { events: timelineEvents } },
        // Three days after the fixture's parkedAt; the age is derived from it.
        { provide: NowTickService, useValue: { now: signal(Date.parse('2026-09-14T14:44:00.000Z')) } },
      ],
    });
  });

  it('renders nothing when the card is not parked', () => {
    expect(render(null).querySelector('[data-testid="parked-blocker"]')).toBeNull();
  });

  it('persists the collapsed park per task and keeps its reason visible', () => {
    const first = TestBed.createComponent(ParkedBlockerComponent);
    first.componentRef.setInput('detail', detail(park()));
    first.detectChanges();
    (first.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('[data-testid="parked-blocker-toggle"]')!.click();
    first.detectChanges();
    expect((first.nativeElement as HTMLElement).querySelector('[data-testid="parked-blocker-question"]')).toBeNull();
    expect((first.nativeElement as HTMLElement).querySelector('[data-testid="parked-blocker-collapsed-reason"]')?.textContent).toContain('requires operator input');
    first.destroy();

    const second = TestBed.createComponent(ParkedBlockerComponent);
    second.componentRef.setInput('detail', detail(park()));
    second.detectChanges();
    expect((second.nativeElement as HTMLElement).querySelector('[data-testid="parked-blocker-toggle"]')?.getAttribute('aria-expanded')).toBe('false');
    expect((second.nativeElement as HTMLElement).querySelector('[data-testid="parked-blocker-question"]')).toBeNull();
  });

  it('shows when it was parked, the park type, the question, the options, and the document', () => {
    const el = render(park());

    expect(text(el, 'parked-blocker-type')).toBe('Agent needs input');
    expect(text(el, 'parked-blocker-since')).toContain('3 days');
    expect(text(el, 'parked-blocker-since')).toContain('2026-09-11');
    expect(text(el, 'parked-blocker-question')).toBe(
      'Should the Studio backend be reached through the Connector or over the LAN?',
    );
    expect(text(el, 'parked-blocker-question-id')).toContain('choose-connector-vs-lan-deployment-strategy');

    const options = el.querySelectorAll('[data-testid="parked-blocker-options"] li');
    expect(options.length).toBe(3);
    expect(options[0].textContent).toContain('Managed connector.');
    expect(options[0].querySelector('[data-testid="parked-blocker-option-recommended"]')).not.toBeNull();

    expect(text(el, 'parked-blocker-condition')).toContain('Only a person can clear this park');
    expect(text(el, 'parked-blocker-document')).toBe(
      'docs/operations/setup/docker-compose-connector-gap.md',
    );
    expect(text(el, 'parked-blocker-artifact')).toBe('results/needs-input.md');
  });

  it('opens the document the run named in the project wiki', () => {
    const el = render(park());
    el.querySelector<HTMLButtonElement>('[data-testid="parked-blocker-document"]')!.click();

    expect(opened).toEqual([{ relPath: 'docs/operations/setup/docker-compose-connector-gap.md' }]);
  });

  it('states the gap when the parking run supplied only a slug', () => {
    const el = render(park({
      cause: 'escalated',
      causeDetail: 'agent-needs-input',
      decision: { questionId: 'choose-primary-column', question: '', options: [], documents: [], decisionCardKey: null },
    }));

    expect(el.querySelector('[data-testid="parked-blocker-question"]')).toBeNull();
    expect(text(el, 'parked-blocker-question-missing')).toBe(
      'The run that parked this card stated no question, only the identifier below. '
        + 'Expand the session context to see what it was waiting for.',
    );
    expect(text(el, 'parked-blocker-question-id')).toContain('choose-primary-column');
  });

  it('claims no parking run when the cause is unknown and names no identifier that is absent', () => {
    const el = render(park({ decision: null }));

    expect(text(el, 'parked-blocker-question-missing')).toBe('No question was recorded for this park.');
    expect(el.textContent).not.toContain('parking run');
    expect(el.querySelector('[data-testid="parked-blocker-question-id"]')).toBeNull();
  });

  /** AGT-3586: DEMO-6 from walkthrough AGT-3561 (F11). */
  function auditPark(overrides: Partial<ParkedBlockerStatus> = {}): ParkedBlockerStatus {
    return park({
      blockerType: 'operator-decision',
      reason: 'missing-delivery: Recover the delivery, resolve the integration failure and rerun the gate.',
      cause: 'acceptance-integration-failed',
      causeDetail: 'missing-delivery',
      decision: null,
      needsInputFile: null,
      lastEvaluatedAt: null,
      evaluationAgeSeconds: null,
      evaluationStale: true,
      detail: 'No recall sweep has evaluated this blocker yet.',
      ...overrides,
    });
  }

  it('says the acceptance audit parked the card, states the reason once and suggests an offered button', () => {
    const el = render(auditPark());
    const body = el.textContent ?? '';

    expect(text(el, 'parked-blocker-type')).toBe('Acceptance audit');
    expect(text(el, 'parked-blocker-statement')).toBe(
      'The acceptance audit parked this card. '
        + 'No delivery branch was found for this card, so its work cannot be integrated.',
    );
    expect(el.querySelector('[data-testid="parked-blocker-question-missing"]')).toBeNull();
    expect(el.querySelector('[data-testid="parked-blocker-reason"]')).toBeNull();
    expect(body).not.toContain('parking run');
    expect(body).not.toContain('missing-delivery');
    expect(body).not.toContain('rerun the gate');
    expect(body.split('No delivery branch was found')).toHaveLength(2);
    expect(text(el, 'parked-blocker-next-step')).toBe(
      'Use Continue (reissue) to build and deliver the work again, or Abort if the work is no longer needed.',
    );
  });

  it('names the Human Review button when the audit parks the card there', () => {
    const el = render(auditPark({ lane: '5-human-review' }), '5-human-review');

    expect(text(el, 'parked-blocker-next-step')).toBe(
      'Use Send back to Ready (re-do) to build and deliver the work again.',
    );
  });

  it('hides a park receipt from an earlier lane after the card reaches Human Review', () => {
    const el = render(auditPark({ lane: '5e-escalated' }), '5-human-review');
    expect(el.querySelector('[data-testid="parked-blocker"]')).toBeNull();
  });

  it('omits a cause-free park placeholder from an otherwise reviewable card', () => {
    const el = render(auditPark({
      lane: '5-human-review', reason: 'operator-move: Parked with no recorded cause.',
      cause: 'operator-decision', causeDetail: null, decision: null,
      conditionDescription: 'Only a person can clear this park; no automatic precondition is recorded.',
    }), '5-human-review');
    expect(el.querySelector('[data-testid="parked-blocker"]')).toBeNull();
  });

  it('keeps a recorded decision visible even with a generic park reason', () => {
    const el = render(auditPark({
      lane: '5-human-review', reason: 'Parked with no recorded cause.',
      decision: park().decision,
    }), '5-human-review');
    expect(el.querySelector('[data-testid="parked-blocker"]')).not.toBeNull();
  });

  it('reads the cause from the lane-change row when the marker predates it', () => {
    timelineEvents.set([{
      ts: '2026-09-11T14:44:00.000Z', kind: 'lane_changed', actor: 'system', summary: '6-completed → 5e-escalated',
      details: { to: '5e-escalated', cause: 'acceptance-integration-failed', causeDetail: 'missing-delivery' },
    }]);
    const el = render(auditPark({ cause: undefined, causeDetail: undefined }));

    expect(text(el, 'parked-blocker-type')).toBe('Acceptance audit');
    expect(text(el, 'parked-blocker-statement')).toContain('The acceptance audit parked this card.');
  });

  it('says once that no sweep has evaluated the blocker', () => {
    const el = render(auditPark());

    expect(text(el, 'parked-blocker-evaluation')).toBe('No sweep has evaluated this blocker yet.');
  });

  it('shows the park reason and clearing condition once', () => {
    const el = render(park({ decision: null }));
    expect(el.querySelector('[data-testid="parked-blocker-open-items"]')).toBeNull();
    expect(el.querySelectorAll('[data-testid="parked-blocker-reason"]')).toHaveLength(1);
    expect(el.querySelectorAll('[data-testid="parked-blocker-condition"]')).toHaveLength(1);
  });

  it('marks a park as a decision so the surface reads distinctly from a failure', () => {
    expect(render(park()).querySelector('[data-testid="parked-blocker"]')!
      .getAttribute('data-decision')).toBe('true');
    expect(render(park({ blockerType: 'infra-crash', requiresDecisionCard: false }))
      .querySelector('[data-testid="parked-blocker"]')!.getAttribute('data-decision')).toBeNull();
  });

  it('keeps session context collapsed and does not load it on card render', () => {
    const el = render(park());

    expect(el.querySelector('[data-testid="parked-session-context"]')).toBeNull();
    expect(taskService.getRunTimeline).not.toHaveBeenCalled();
    expect(taskService.getSessionEvents).not.toHaveBeenCalled();
    expect(taskService.getJobOutput).not.toHaveBeenCalled();
  });

  it('loads once on expansion and renders the actual model, run prompt, and transcript', () => {
    const fixture = TestBed.createComponent(ParkedBlockerComponent);
    fixture.componentRef.setInput('detail', detail(park()));
    fixture.detectChanges();

    fixture.nativeElement.querySelector('[data-testid="parked-session-toggle"]').click();
    fixture.detectChanges();

    expect(taskService.getRunTimeline).toHaveBeenCalledTimes(1);
    expect(taskService.getSessionEvents).toHaveBeenCalledTimes(1);
    expect(taskService.getJobOutput).toHaveBeenCalledTimes(1);
    expect(text(fixture.nativeElement, 'parked-session-model')).toContain('codex');
    expect(text(fixture.nativeElement, 'parked-session-model')).toContain('gpt-6-sol');
    expect(text(fixture.nativeElement, 'parked-session-model')).toContain('high');
    expect(text(fixture.nativeElement, 'parked-session-prompt')).toBe('Choose the managed connector.');
    expect(fixture.nativeElement.querySelector('[data-testid="parked-session-transcript"]')).not.toBeNull();
    expect(JSON.parse(localStorage.getItem('taskboard.parkedSession.expanded.v1') ?? '{}'))
      .toEqual({ 'AGT-2736': true });
  });

  it('renders an explicit empty transcript state', () => {
    taskService.getJobOutput.mockReturnValue(of([]));
    const fixture = TestBed.createComponent(ParkedBlockerComponent);
    fixture.componentRef.setInput('detail', detail(park()));
    fixture.detectChanges();
    fixture.nativeElement.querySelector('[data-testid="parked-session-toggle"]').click();
    fixture.detectChanges();

    expect(text(fixture.nativeElement, 'parked-session-transcript-empty')).toContain('No chat transcript');
  });

  it('resolves the last run before the park and the follow-up that started it', () => {
    const first = run({ index: 1, startedAt: '2026-09-11T13:00:00Z' });
    const parking = run();
    const later = run({ index: 3, startedAt: '2026-09-12T13:00:00Z' });

    expect(resolveParkingRun([first, parking, later], '2026-09-11T14:44:00Z')).toBe(parking);
    expect(resolveParkingSessionEvent([
      { ...sessionEvents().events[0], ts: '2026-09-11T13:00:00Z', cli: 'claude' },
      sessionEvents().events[0],
      { ...sessionEvents().events[0], ts: '2026-09-12T13:00:00Z', cli: 'copilot' },
    ], '2026-09-11T14:44:00Z')?.cli).toBe('codex');
    expect(resolveRunPrompt(parking, 'Initial', detail(park()).promptHistory, [])).toBe('Choose the managed connector.');
  });

  it('cancels the prior card request so a late response cannot overwrite the next card', () => {
    const oldTimeline = new Subject<RunTimeline>();
    const oldSessionEvents = new Subject<SessionEventsResponse>();
    const oldOutput = new Subject<CliOutputLine[]>();
    const nextRun = run({ model: 'gpt-6-astra', thinkingLevel: 'xhigh' });

    taskService.getRunTimeline.mockImplementation((id: string) =>
      id === 'AGT-2736' ? oldTimeline : of(timeline([nextRun])));
    taskService.getSessionEvents.mockImplementation((id: string) =>
      id === 'AGT-2736' ? oldSessionEvents : of(sessionEvents('claude')));
    taskService.getJobOutput.mockImplementation((id: string) =>
      id === 'AGT-2736' ? oldOutput : of([
        { timestamp: '2026-09-11T14:41:00Z', stream: 'stdout', text: 'Context for the next card.' },
      ]));
    localStorage.setItem('taskboard.parkedSession.expanded.v1', JSON.stringify({
      'AGT-2736': true,
      'AGT-4000': true,
    }));

    const fixture = TestBed.createComponent(ParkedBlockerComponent);
    fixture.componentRef.setInput('detail', detail(park(), 'AGT-2736'));
    fixture.detectChanges();
    fixture.componentRef.setInput('detail', detail(park(), 'AGT-4000'));
    fixture.detectChanges();

    expect(text(fixture.nativeElement, 'parked-session-model')).toContain('gpt-6-astra');
    expect(text(fixture.nativeElement, 'parked-session-model')).toContain('claude');

    oldTimeline.next(timeline([run({ model: 'stale-model' })]));
    oldTimeline.complete();
    oldSessionEvents.next(sessionEvents('stale-cli'));
    oldSessionEvents.complete();
    oldOutput.next([]);
    oldOutput.complete();
    fixture.detectChanges();

    expect(text(fixture.nativeElement, 'parked-session-model')).toContain('gpt-6-astra');
    expect(text(fixture.nativeElement, 'parked-session-model')).not.toContain('stale-model');
    expect(text(fixture.nativeElement, 'parked-session-model')).not.toContain('stale-cli');
    expect(oldTimeline.observed).toBe(false);
    expect(oldSessionEvents.observed).toBe(false);
    expect(oldOutput.observed).toBe(false);
  });

  it('cancels an in-flight session request when the component is destroyed', () => {
    const pendingTimeline = new Subject<RunTimeline>();
    const pendingSessionEvents = new Subject<SessionEventsResponse>();
    const pendingOutput = new Subject<CliOutputLine[]>();
    taskService.getRunTimeline.mockReturnValue(pendingTimeline);
    taskService.getSessionEvents.mockReturnValue(pendingSessionEvents);
    taskService.getJobOutput.mockReturnValue(pendingOutput);
    localStorage.setItem('taskboard.parkedSession.expanded.v1', JSON.stringify({ 'AGT-2736': true }));

    const fixture = TestBed.createComponent(ParkedBlockerComponent);
    fixture.componentRef.setInput('detail', detail(park()));
    fixture.detectChanges();

    expect(pendingTimeline.observed).toBe(true);
    expect(pendingSessionEvents.observed).toBe(true);
    expect(pendingOutput.observed).toBe(true);

    fixture.destroy();

    expect(pendingTimeline.observed).toBe(false);
    expect(pendingSessionEvents.observed).toBe(false);
    expect(pendingOutput.observed).toBe(false);
  });
});
