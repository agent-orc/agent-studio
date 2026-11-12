import { provideZonelessChangeDetection, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { describe, expect, it, beforeEach, vi } from 'vitest';
import type { TaskInfo } from '../../models/task.model';
import { TaskLiveStatusComponent } from './task-live-status.component';
import { RemoteHostsService } from '../../features/remote-hosts';
import { TaskReferenceNavigationService } from '../../services/task-reference-navigation.service';
import { TaskService } from '../../services/task.service';
import { of } from 'rxjs';
import { BatchGateService } from '../../services/batch-gate.service';
import type { BatchGateState } from '../../models/batch-gate.model';

function task(overrides: Partial<TaskInfo> = {}): TaskInfo {
  const now = Date.now();
  return {
    id: 'AGT-2315',
    taskKey: 'demo::AGT-2315',
    title: 'Show live work',
    state: '4-auto-review',
    order: 1,
    agent: 'codex',
    createdAt: new Date(now - 60_000).toISOString(),
    watchPath: '/workspace',
    projectName: 'demo',
    folderPath: '/workspace/4-auto-review/AGT-2315',
    lastActivity: new Date(now - 20_000).toISOString(),
    sessionName: null,
    model: 'gpt-5.4-mini',
    cliType: 'codex',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    ...overrides,
  };
}

describe('TaskLiveStatusComponent', () => {
  let fixture: ComponentFixture<TaskLiveStatusComponent>;
  const modeCalls: [string, string][] = [];
  const batchState = signal<BatchGateState | null>(null);

  const navigation = { openReferenceOrNotify: vi.fn(() => true) };

  beforeEach(async () => {
    navigation.openReferenceOrNotify.mockClear();
    batchState.set(null);
    await TestBed.configureTestingModule({
      imports: [TaskLiveStatusComponent],
      providers: [provideZonelessChangeDetection(),
        { provide: BatchGateService, useValue: { state: () => batchState.asReadonly(), watch: () => vi.fn() } },
        { provide: TaskService, useValue: {
          setRunnerMode: (project: string, mode: string) => {
            modeCalls.push([project, mode]);
            return of({});
          },
          refresh: vi.fn(),
        } },
        { provide: RemoteHostsService, useValue: { hosts: () => [] } },
        { provide: TaskReferenceNavigationService, useValue: navigation }],
    }).compileComponents();
    fixture = TestBed.createComponent(TaskLiveStatusComponent);
    modeCalls.length = 0;
    batchState.set(null);
  });

  it('shows the queued and running state of a paused batch member', () => {
    const liveStatus = { attempt: 1, activeStep: null, nextSteps: [], queue: { kind: 'review' } };
    fixture.componentRef.setInput('task', task({ liveStatus }));
    batchState.set({ perTaskGates: [{ taskKey: 'demo::AGT-2315', reviewAttemptId: 'r1',
      position: 2, total: 4, batchId: 'batch-1', reason: 'batch batch-1 paused: fresh gate verdict required',
      running: false }] } as BatchGateState);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="task-live-current"]').textContent)
      .toContain('Per-task gate queued (2 of 4) after batch batch-1 paused: fresh gate verdict required');
    batchState.set({ perTaskGates: [{ taskKey: 'demo::AGT-2315', reviewAttemptId: 'r1',
      position: 1, total: 1, reason: 'planner selected a per-task gate', running: false }] } as BatchGateState);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="task-live-current"]').textContent)
      .toContain('Per-task gate queued (1 of 1) after planner selected a per-task gate');
    batchState.set({ perTaskGates: [{ taskKey: 'demo::AGT-2315', reviewAttemptId: 'r1',
      position: 2, total: 4, batchId: 'batch-1', reason: 'batch batch-1 paused: fresh gate verdict required',
      running: true, host: 'gate-01', startedAtUtc: '2026-11-10T12:00:00Z' }] } as BatchGateState);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="task-live-current"]').textContent)
      .toContain('Per-task gate running on gate-01 since');
    fixture.componentRef.setInput('task', task({ liveStatus: null }));
    batchState.set({ perTaskGates: [{ taskKey: 'demo::AGT-2315', reviewAttemptId: 'r1',
      position: 2, total: 4, batchId: 'batch-1', reason: 'batch batch-1 paused: fresh gate verdict required',
      running: false }] } as BatchGateState);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="task-live-current"]').textContent)
      .toContain('Per-task gate queued (2 of 4) after batch batch-1 paused: fresh gate verdict required');
  });

  it('shows a Ready pickup hold without a previous live run', () => {
    fixture.componentRef.setInput('task', task({
      state: '2-ready', liveStatus: null,
      pickupHold: { mechanism: 'pickup-policy', classification: 'satisfiable-soon',
        reason: 'Waiting for the runner.', sinceUtc: new Date().toISOString(),
        unsatisfiable: false, resolutions: [] },
    }));
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="pickup-hold-reason"]')?.textContent)
      .toContain('Waiting for the runner.');
  });

  it('renders the active step with duration, host, model, CLI and next steps', () => {
    const now = Date.now();
    fixture.componentRef.setInput('task', task({
      executionLocation: {
        state: 'remote-running',
        executionKind: 'remote',
        hostDisplayName: 'agent-runner-01',
        connectionState: 'connected',
        leaseState: 'active',
        trustReason: 'lease',
      },
      liveStatus: {
        attempt: 4,
        activeStep: {
          stepId: 'aspect-tests-and-evidence',
          displayName: 'Tests and evidence',
          kind: 'aspect',
          startedAt: new Date(now - 40_000).toISOString(),
          model: 'gpt-5.4-mini',
          cliType: 'codex',
        },
        nextSteps: [
          { stepId: 'post-code-review-grade', displayName: 'Code-review quality grade' },
          { stepId: 'post-build-test-gate', displayName: 'Build/test gate' },
          { stepId: 'post-integrate-merge', displayName: 'Integrate merge' },
        ],
        latestEventAt: new Date(now - 40_000).toISOString(),
      },
    }));
    fixture.detectChanges();

    const root = fixture.nativeElement.querySelector('[data-testid="task-live-status"]') as HTMLElement;
    expect(root.dataset['liveTone']).toBe('active');
    expect(root.textContent).toContain('Review aspect · Tests and evidence');
    expect(root.textContent).toMatch(/running (?:39|40)s/);
    expect(root.textContent).toContain('agent-runner-01');
    expect(root.textContent).toContain('gpt-5.4-mini');
    expect(root.textContent).toContain('via Codex');
    expect(root.textContent).toContain('Code-review quality grade → Build/test gate → Integrate merge');
  });

  it('shows an honest queue position when no step is active', () => {
    fixture.componentRef.setInput('task', task({
      liveStatus: {
        attempt: 2,
        activeStep: null,
        nextSteps: [{ stepId: 'aspect-requirement-fit', displayName: 'Requirement fit' }],
        queue: { kind: 'review', position: 3 },
        latestEventAt: new Date().toISOString(),
      },
    }));
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('Waiting for review slot · position 3');
    expect(fixture.nativeElement.textContent).toContain('Next');
    expect(fixture.nativeElement.querySelector('[data-testid="task-live-next"]')?.textContent).toContain('Review');
    expect(fixture.nativeElement.textContent).not.toContain('Requirement fit');
  });

  it.each([
    { reason: 'waiting for batch gate verdict', current: 'Waiting for batch verdict', next: 'Batch verdict' },
    { reason: 'waiting for delivery integration', current: 'Waiting for delivery integration', next: 'Delivery integration' },
    { reason: 'review ended; operator action needed', current: 'Waiting for operator decision', next: 'Operator decision' },
  ])('uses the Post Processing wait for NEXT: $next', ({ reason, current, next }) => {
    fixture.componentRef.setInput('task', task({
      liveStatus: {
        attempt: 2, activeStep: null,
        nextSteps: [{ stepId: 'pre-loop-guard', displayName: 'Loop check' }],
        queue: { kind: 'review', reason }, latestEventAt: null,
      },
    }));
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="task-live-current"]')?.textContent).toContain(current);
    expect(root.querySelector('[data-testid="task-live-next"]')?.textContent).toContain(next);
    expect(root.textContent).not.toContain('Loop check');
  });

  it('takes the batch verdict from the shared batch member state', () => {
    batchState.set({
      plan: { decision: 'wait', reason: 'batch formed', proposedMembers: [], targetSize: 2, upcoming: [] },
      pending: [], batches: [],
      members: { 'demo::AGT-2315': { status: 'in-batch', batchId: 'batch-1', phase: 'gate-running' } },
    });
    fixture.componentRef.setInput('task', task({
      liveStatus: {
        attempt: 2, activeStep: null,
        nextSteps: [{ stepId: 'pre-loop-guard', displayName: 'Loop check' }],
        queue: { kind: 'review', position: 1 }, latestEventAt: null,
      },
    }));
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="task-live-current"]')?.textContent).toContain('Waiting for batch verdict');
    expect(root.querySelector('[data-testid="task-live-next"]')?.textContent).toContain('Batch verdict');
    expect(root.textContent).not.toContain('Loop check');
  });

  it('shows the owner decision in Human Review without a pipeline record', () => {
    fixture.componentRef.setInput('task', task({ state: '5-human-review', liveStatus: null }));
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="task-live-current"]')?.textContent).toContain('Waiting for owner review');
    expect(root.querySelector('[data-testid="task-live-next"]')?.textContent).toContain('Owner decision');
  });

  it('does not claim the next attempt is running before any event exists', () => {
    fixture.componentRef.setInput('variant', 'detail');
    fixture.componentRef.setInput('task', task({
      state: '2-ready', lastActivity: '',
      liveStatus: { attempt: 2, activeStep: null, nextSteps: [], queue: null, latestEventAt: null },
    }));
    fixture.detectChanges();
    const live = fixture.nativeElement.querySelector('[data-testid="task-live-status"]') as HTMLElement;
    expect(live.textContent).not.toContain('Attempt 2');
    expect(live.getAttribute('data-attempt')).toBeNull();
  });

  // AGT-3617 (playthrough AGT-3560, H1): a card held behind an open cause
  // breaker said "Waiting for review slot · position 1" while the review host
  // was idle; the claim skips it, so the hold is the CURRENT state.
  it('names the cause hold instead of a review slot position, with a link to the cause card', () => {
    const since = new Date(Date.now() - 12 * 60_000).toISOString();
    fixture.componentRef.setInput('task', task({
      liveStatus: {
        attempt: 2, activeStep: null, nextSteps: [],
        queue: { kind: 'review', position: 1 },
        latestEventAt: new Date().toISOString(),
      },
      causeWait: {
        causeKey: 'DEMO-2', fingerprint: 'fp', failureClass: 'ReviewInfra/TestReportUnavailable',
        since, reason: 'waiting for DEMO-2', summary: 'review cannot read the test report',
      },
    }));
    fixture.detectChanges();

    const root = fixture.nativeElement.querySelector('[data-testid="task-live-status"]') as HTMLElement;
    expect(root.dataset['liveTone']).toBe('waiting');
    expect(root.querySelector('[data-testid="task-live-hold"]')?.textContent?.replace(/\s+/g, ' ').trim())
      .toBe('Held behind DEMO-2: review cannot read the test report');
    expect(root.textContent).not.toContain('Waiting for review slot');
    expect(root.textContent).toMatch(/held for 1[12]m/);

    const cardClick = vi.fn();
    root.addEventListener('click', cardClick);
    (root.querySelector('[data-testid="task-live-hold-link"]') as HTMLButtonElement).click();
    expect(navigation.openReferenceOrNotify).toHaveBeenCalledWith('DEMO-2');
    // On a board card the link opens the cause card, not the held card.
    expect(cardClick).not.toHaveBeenCalled();
  });

  it('shows the cause hold even before the card has a live status', () => {
    fixture.componentRef.setInput('variant', 'detail');
    fixture.componentRef.setInput('task', task({
      liveStatus: null,
      causeWait: {
        causeKey: 'DEMO-2', fingerprint: 'fp', failureClass: 'ReviewInfra/ToolUnavailable',
        since: new Date().toISOString(), reason: 'waiting for DEMO-2',
      },
    }));
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="task-live-hold"]')?.textContent).toContain('Held behind');
    expect(root.querySelector('[data-testid="task-live-hold"]')?.textContent)
      .toContain('review keeps failing for the same cause');
    // No live status, so no attempt is claimed.
    expect(root.textContent).not.toContain('Attempt');
  });

  it('does not show a cause hold outside Post Processing', () => {
    fixture.componentRef.setInput('task', task({
      state: '5-human-review',
      liveStatus: null,
      causeWait: {
        causeKey: 'DEMO-2', fingerprint: 'fp', failureClass: 'ReviewInfra/ToolUnavailable',
        since: new Date().toISOString(), reason: 'waiting for DEMO-2',
      },
    }));
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="task-live-hold"]')).toBeNull();
  });

  it('names the wait reason instead of an empty queue when no slot position is known', () => {
    fixture.componentRef.setInput('task', task({
      liveStatus: {
        attempt: 1,
        activeStep: null,
        nextSteps: [],
        queue: {
          kind: 'review',
          reason: 'waiting for review executor: agent-runner-01-review is registered and active',
        },
        latestEventAt: new Date().toISOString(),
      },
    }));
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain(
      'Waiting for review slot · waiting for review executor: agent-runner-01-review is registered and active');
  });

  it('names the executor that holds the review lease instead of a slot wait', () => {
    const since = new Date(Date.now() - 5 * 60_000);
    fixture.componentRef.setInput('task', task({
      liveStatus: {
        attempt: 1,
        activeStep: null,
        nextSteps: [],
        queue: {
          kind: 'review',
          state: 'reviewing',
          executorId: 'kori-review',
          since: since.toISOString(),
          reason: 'reviewing on kori-review since 2026-10-12 17:46:00Z',
        },
        latestEventAt: new Date().toISOString(),
      },
    }));
    fixture.detectChanges();

    const root = fixture.nativeElement.querySelector('[data-testid="task-live-status"]') as HTMLElement;
    expect(root.dataset['liveTone']).toBe('active');
    expect(root.textContent).toContain(
      `Reviewing on kori-review since ${since.toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit' })}`);
    expect(root.textContent).not.toContain('Waiting for review slot');
    expect(root.textContent).not.toContain('waiting for review executor');
  });

  it('calls out a review that nothing will pick up again as stalled', () => {
    fixture.componentRef.setInput('task', task({
      liveStatus: {
        attempt: 1,
        activeStep: null,
        nextSteps: [],
        queue: {
          kind: 'review',
          state: 'stalled',
          outcome: 'infrastructureFailure',
          reason: 'review ended (infrastructureFailure); no new review attempt; operator decision needed',
        },
        latestEventAt: new Date().toISOString(),
      },
    }));
    fixture.detectChanges();

    const root = fixture.nativeElement.querySelector('[data-testid="task-live-status"]') as HTMLElement;
    expect(root.dataset['liveTone']).toBe('stalled');
    expect(root.querySelector('[data-testid="task-live-current"]')?.textContent)
      .toContain('Review stalled · operator decision needed');
    expect(root.textContent).toContain(
      'review ended (infrastructureFailure); no new review attempt; operator decision needed');
    expect(root.textContent).not.toContain('Waiting for review slot');
  });

  // AGT-3690 (S5-F14): a review host killed mid-review. The CURRENT line names
  // the restart, the host (as a link to its detail) and the automatic retry.
  it('names a review host restart, links the host, and states the next automatic retry', () => {
    const lostAt = '2026-11-10T01:27:16Z';
    const dueAt = '2026-11-10T01:28:16Z';
    const clock = (iso: string) => new Date(iso).toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit' });
    fixture.componentRef.setInput('task', task({
      liveStatus: {
        attempt: 1,
        activeStep: null,
        nextSteps: [],
        queue: {
          kind: 'review',
          state: 'retry-scheduled',
          outcome: 'infrastructureFailure',
          reason: 'Review lost: review host restarted (lab-review-host, 2026-11-10 01:27:16Z). Automatic retry 1 of 3 at 2026-11-10 01:28:16Z.',
          lost: { cause: 'host-restarted', classification: 'ExecutorRestarted', hostId: 'lab-review-host',
            executorId: 'first-run-review', at: lostAt },
          retry: { number: 1, budget: 3, dueAt },
        },
        latestEventAt: new Date().toISOString(),
      },
    }));
    fixture.detectChanges();

    const root = fixture.nativeElement.querySelector('[data-testid="task-live-status"]') as HTMLElement;
    expect(root.dataset['liveTone']).toBe('waiting');
    const headline = root.querySelector('[data-testid="task-live-review-loss"]') as HTMLElement;
    expect(headline.textContent?.replace(/\s+/g, ' ').trim()).toBe(
      `Review lost: review host restarted (lab-review-host, ${clock(lostAt)}). Automatic retry 1 of 3 at ${clock(dueAt)}.`);
    const link = root.querySelector('[data-testid="task-live-host-link"]') as HTMLAnchorElement;
    expect(link.textContent).toBe('lab-review-host');
    expect(link.getAttribute('href')).toBe('#/workspace/settings/environment?host=first-run-review');
    expect(root.textContent).not.toContain('Review stalled');
    expect(root.textContent).not.toContain('Waiting for review slot');
  });

  it('opens the host from the link without opening the card', () => {
    const previousHash = window.location.hash;
    fixture.componentRef.setInput('task', task({
      liveStatus: {
        attempt: 1,
        activeStep: null,
        nextSteps: [],
        queue: {
          kind: 'review',
          state: 'retry-scheduled',
          lost: { cause: 'host-restarted', hostId: 'lab-review-host', executorId: 'first-run-review',
            at: new Date().toISOString() },
          retry: { number: 2, budget: 3, dueAt: new Date(Date.now() + 180_000).toISOString() },
        },
      },
    }));
    fixture.detectChanges();
    const parentClick = vi.fn();
    (fixture.nativeElement as HTMLElement).addEventListener('click', parentClick);

    (fixture.nativeElement.querySelector('[data-testid="task-live-host-link"]') as HTMLAnchorElement).click();

    expect(parentClick).not.toHaveBeenCalled();
    expect(window.location.hash).toContain('/workspace/settings/environment?host=first-run-review');
    window.location.hash = previousHash;
  });

  it('keeps a review host restart without a retry stalled and still names the restart', () => {
    fixture.componentRef.setInput('task', task({
      liveStatus: {
        attempt: 1,
        activeStep: null,
        nextSteps: [],
        queue: {
          kind: 'review',
          state: 'stalled',
          outcome: 'infrastructureFailure',
          lost: { cause: 'host-restarted', hostId: 'lab-review-host', at: new Date().toISOString() },
        },
      },
    }));
    fixture.detectChanges();

    const root = fixture.nativeElement.querySelector('[data-testid="task-live-status"]') as HTMLElement;
    expect(root.dataset['liveTone']).toBe('stalled');
    expect(root.querySelector('[data-testid="task-live-review-loss"]')?.textContent)
      .toContain('No automatic retry is scheduled · operator decision needed');
  });

  it('keeps detail status to compact CURRENT and NEXT rows', () => {
    fixture.componentRef.setInput('variant', 'detail');
    fixture.componentRef.setInput('task', task({
      state: '2-ready',
      liveStatus: {
        attempt: 1,
        activeStep: null,
        nextSteps: [{ stepId: 'pre-loop-guard', displayName: 'Loop check' }],
        queue: { kind: 'runner', position: 3 },
        latestEventAt: new Date().toISOString(),
      },
    }));
    fixture.detectChanges();

    const root = fixture.nativeElement.querySelector('[data-testid="task-live-status"]') as HTMLElement;
    expect(root.querySelectorAll(':scope > div')).toHaveLength(2);
    expect(root.querySelector('[data-testid="task-live-current"]')?.textContent)
      .toContain('Queued for runner · position 3');
    expect(root.querySelector('[data-testid="task-live-detail-inline"]')?.textContent)
      .toContain('Last activity');
    expect(root.querySelector('[data-testid="task-live-next"]')?.textContent)
      .toContain('Loop check');
  });

  // AGT-3579: the runner's own reason replaces the bare slot wait.
  it.each([
    ['manual-mode', 'Not picked · auto-pickup is off · position 2', 'Auto-pickup is off; start the card or switch the project to auto.'],
    ['slots-full', 'Waiting for runner slot · slots full · position 2', 'All 2 runner slots are occupied.'],
    ['quota-defer', 'Skipped · quota defer · position 2', 'codex 5h window exhausted until 14:00'],
  ])('names the runner reason %s with its sentence', (code, headline, reason) => {
    fixture.componentRef.setInput('task', task({
      state: '2-ready',
      liveStatus: {
        attempt: 1,
        activeStep: null,
        nextSteps: [],
        queue: { kind: 'runner', position: 2, reasonCode: code, reason },
        latestEventAt: new Date().toISOString(),
      },
    }));
    fixture.detectChanges();

    const root = fixture.nativeElement.querySelector('[data-testid="task-live-status"]') as HTMLElement;
    expect(root.getAttribute('data-wait-reason')).toBe(code);
    expect(root.getAttribute('data-live-tone')).toBe('waiting');
    expect(root.querySelector('[data-testid="task-live-current"]')?.textContent).toContain(headline);
    expect(root.querySelector('.task-live__detail')?.textContent).toContain(reason);
  });

  it('offers Auto inline for a remote Ready card held in Manual mode', () => {
    fixture.componentRef.setInput('task', task({
      state: '2-ready',
      executionLocation: {
        state: 'no-active-execution', executionKind: 'none',
        configuredRunnerId: 'first-run-coding', hostDisplayName: 'first-run-coding',
      },
      liveStatus: {
        attempt: 1, activeStep: null, nextSteps: [], latestEventAt: null,
        queue: { kind: 'runner', position: 1, reasonCode: 'manual-mode', reason: 'Auto-pickup is off.' },
      },
    }));
    fixture.detectChanges();

    const current = fixture.nativeElement.querySelector('[data-testid="task-live-current"]') as HTMLElement;
    expect(current.textContent).toContain('Pickup is Manual: switch to Auto to let first-run-coding claim it');
    expect(current.textContent).not.toContain('Waiting for runner slot');
    const action = current.querySelector('[data-testid="task-live-switch-auto"]') as HTMLButtonElement;
    expect(action.textContent).toContain('Switch to Auto');
    action.click();
    expect(modeCalls).toEqual([['demo', 'auto-continuous']]);
    expect(TestBed.inject(TaskService).refresh).toHaveBeenCalledWith(true);
  });

  it('shows breaker cause and end time without offering Switch to Auto', () => {
    const reason = 'Auto-pickup is cooling down after a circuit-breaker trip (quota failure) and resumes at 2026-11-10T12:00:00.0000000Z.';
    fixture.componentRef.setInput('task', task({
      state: '2-ready',
      executionLocation: {
        state: 'no-active-execution', executionKind: 'none',
        configuredRunnerId: 'first-run-coding', hostDisplayName: 'first-run-coding',
      },
      liveStatus: { attempt: 1, activeStep: null, nextSteps: [], latestEventAt: null,
        queue: { kind: 'runner', position: 1, reasonCode: 'breaker-cooldown', reason } },
    }));
    fixture.detectChanges();

    const root = fixture.nativeElement.querySelector('[data-testid="task-live-status"]') as HTMLElement;
    expect(root.textContent).toContain('Not picked · breaker cooldown');
    expect(root.textContent).toContain('quota failure');
    expect(root.textContent).toContain('2026-11-10T12:00:00.0000000Z');
    expect(root.querySelector('[data-testid="task-live-switch-auto"]')).toBeNull();
    expect(modeCalls).toEqual([]);
  });

  it('names an integration wait without describing a review slot', () => {
    fixture.componentRef.setInput('task', task({
      liveStatus: { attempt: 1, activeStep: null, nextSteps: [], latestEventAt: null,
        queue: { kind: 'review', state: 'integration', reason: 'waiting for integration recovery' } },
    }));
    fixture.detectChanges();
    const root = fixture.nativeElement.querySelector('[data-testid="task-live-status"]') as HTMLElement;
    expect(root.textContent).toContain('Waiting for delivery integration');
    expect(root.textContent).toContain('waiting for integration recovery');
    expect(root.textContent).not.toContain('Waiting for review slot');
  });

  it('keeps a card hold ahead of a runner reason', () => {
    fixture.componentRef.setInput('task', task({
      state: '2-ready',
      pickupHold: { mechanism: 'crash-backoff', classification: 'satisfiable-soon',
        reason: 'Cooling down.', sinceUtc: new Date().toISOString(), unsatisfiable: false, resolutions: [] },
      liveStatus: {
        attempt: 1, activeStep: null, nextSteps: [],
        queue: { kind: 'runner', position: 1, reasonCode: 'manual-mode', reason: 'Auto-pickup is off.' },
        latestEventAt: new Date().toISOString(),
      },
    }));
    fixture.detectChanges();

    const root = fixture.nativeElement.querySelector('[data-testid="task-live-status"]') as HTMLElement;
    expect(root.getAttribute('data-wait-reason')).toBeNull();
    expect(root.querySelector('[data-testid="task-live-current"]')?.textContent).toContain('Crash cooldown');
  });

  it('shows one dependency wait instead of a stale runner position, then flips to the slot after fulfillment', () => {
    const nextSteps = [{ stepId: 'core-agent-run', displayName: 'Agent execution' }];
    const open = task({
      state: '2-ready',
      waitsOn: {
        blocked: true,
        cycleDetected: false,
        items: [{
          key: 'AGT-2534',
          resolved: true,
          fulfilled: false,
          waitingForRelease: false,
          targetState: '5-human-review',
        }],
      },
      liveStatus: {
        attempt: 2,
        activeStep: null,
        nextSteps,
        queue: { kind: 'runner', position: 4 },
        latestEventAt: new Date().toISOString(),
      },
    });
    fixture.componentRef.setInput('task', open);
    fixture.detectChanges();

    const current = fixture.nativeElement.querySelector('[data-testid="task-live-current"]') as HTMLElement;
    expect(current.textContent).toContain('waits for completion: AGT-2534');
    expect(current.textContent).not.toContain('runner slot');
    expect(current.textContent).not.toContain('position 4');
    expect(fixture.nativeElement.textContent).toContain('Next');
    expect(fixture.nativeElement.textContent).toContain('Dependency resolution → Runner pickup');

    fixture.componentRef.setInput('task', {
      ...open,
      waitsOn: {
        blocked: false,
        cycleDetected: false,
        items: [{
          ...open.waitsOn!.items[0],
          fulfilled: true,
          targetState: '6-completed',
        }],
      },
      liveStatus: {
        ...open.liveStatus!,
        queue: { kind: 'runner', position: 3 },
      },
    });
    fixture.detectChanges();

    expect(current.textContent).toContain('Queued for runner · position 3');
    expect(current.textContent).not.toContain('waits for completion');
    expect(fixture.nativeElement.textContent).toContain('Agent execution');
  });

  // AGT-2378: `runActivity` is classified from the local slot registry + local
  // CLI execution, so a remote run (fenced lease + attempt records, no local
  // process) arrives as `no-active-run` between two pipeline steps. The strip
  // must not claim "No active run" while the lease says otherwise.
  it('does not claim "No active run" for a remote run between steps', () => {
    const recent = new Date(Date.now() - 15_000).toISOString();
    fixture.componentRef.setInput('task', task({
      state: '3-progress',
      lastActivity: recent,
      runActivity: { kind: 'no-active-run', attempt: 0 },
      runner: {
        runnerId: 'agent-runner-01',
        runnerName: 'agent-runner-01',
        hostname: 'agent-runner-01',
        backendName: 'stable',
        isRemote: true,
        leaseId: 'lease-1',
        fencingToken: 7,
        acquiredAt: recent,
      },
      executionLocation: {
        state: 'remote-running',
        executionKind: 'remote',
        hostDisplayName: 'agent-runner-01',
        lastActivityAt: recent,
        connectionState: 'connected',
        leaseState: 'active',
        trustReason: 'lease',
      },
      liveStatus: {
        attempt: 3,
        activeStep: null,
        nextSteps: [{ stepId: 'post-code-review-grade', displayName: 'Code-review quality grade' }],
        queue: null,
        latestEventAt: recent,
      },
    }));
    fixture.detectChanges();

    const root = fixture.nativeElement.querySelector('[data-testid="task-live-status"]') as HTMLElement;
    expect(root.textContent).not.toContain('No active run');
    expect(root.textContent).toContain('Running remote on agent-runner-01');
    expect(root.dataset['liveTone']).toBe('active');
  });

  it('still calls out "No active run" when nothing owns the task', () => {
    const recent = new Date(Date.now() - 15_000).toISOString();
    fixture.componentRef.setInput('task', task({
      state: '3-progress',
      lastActivity: recent,
      runActivity: { kind: 'no-active-run', attempt: 0 },
      runner: null,
      executionLocation: {
        state: 'recovering',
        executionKind: 'none',
        connectionState: 'recovering',
        leaseState: 'none',
        trustReason: 'no owner',
      },
      liveStatus: {
        attempt: 3,
        activeStep: null,
        nextSteps: [],
        queue: null,
        latestEventAt: recent,
      },
    }));
    fixture.detectChanges();

    const root = fixture.nativeElement.querySelector('[data-testid="task-live-status"]') as HTMLElement;
    expect(root.textContent).toContain('No active run');
    expect(root.dataset['liveTone']).toBe('stalled');
  });

  it('shows a fresh remote lease as the current run when no local execution or pipeline step exists', () => {
    const now = Date.now();
    fixture.componentRef.setInput('task', task({
      state: '3-progress',
      execution: null,
      runner: {
        runnerId: 'agent-runner-01@linux-host',
        runnerName: 'agent-runner-01',
        hostname: 'linux-host',
        backendName: 'remote',
        isRemote: true,
        leaseId: 'lease-remote',
        fencingToken: 4,
        acquiredAt: new Date(now - 125_000).toISOString(),
      },
      executionLocation: {
        state: 'remote-running',
        executionKind: 'remote',
        runnerId: 'agent-runner-01@linux-host',
        hostDisplayName: 'agent-runner-01',
        startedAt: new Date(now - 125_000).toISOString(),
        lastHeartbeat: new Date(now - 2_000).toISOString(),
        lastActivityAt: new Date(now - 2_000).toISOString(),
        connectionState: 'connected',
        leaseState: 'active',
        trustReason: 'Fresh fenced lease heartbeat.',
      },
      runActivity: { kind: 'no-active-run', attempt: 0 },
      liveStatus: {
        attempt: 1,
        activeStep: null,
        nextSteps: [{ stepId: 'core-agent-run', displayName: 'Agent run' }],
        queue: null,
        latestEventAt: new Date(now - 2_000).toISOString(),
      },
    }));
    fixture.detectChanges();

    const root = fixture.nativeElement.querySelector('[data-testid="task-live-status"]') as HTMLElement;
    expect(root.dataset['liveTone']).toBe('active');
    expect(root.textContent).toContain('Running remote on agent-runner-01');
    expect(root.textContent).toMatch(/Active for 2m0[45]s/);
    expect(root.textContent).not.toContain('No active run');
    expect(root.textContent).not.toContain('possible hang');
  });

  it('calls out a possible hang after ten minutes without a step or queue', () => {
    const old = new Date(Date.now() - 12 * 60_000).toISOString();
    fixture.componentRef.setInput('task', task({
      lastActivity: old,
      executionLocation: null,
      liveStatus: {
        attempt: 7,
        activeStep: null,
        nextSteps: [{ stepId: 'post-orchestrator-decision', displayName: 'Final review decision' }],
        queue: null,
        latestEventAt: old,
      },
    }));
    fixture.detectChanges();

    const root = fixture.nativeElement.querySelector('[data-testid="task-live-status"]') as HTMLElement;
    expect(root.dataset['liveTone']).toBe('stalled');
    expect(root.textContent).toMatch(/No activity for (?:11m59s|12m00s) · possible hang/);
  });

  it('never flags a possible hang in preparation, however long it has been idle', () => {
    const stale = new Date(Date.now() - 93 * 60 * 60_000).toISOString();
    fixture.componentRef.setInput('task', task({
      state: '1-preparation',
      lastActivity: stale,
      executionLocation: null,
      runActivity: { kind: 'no-active-run', attempt: 0 },
      liveStatus: {
        attempt: 1,
        activeStep: null,
        nextSteps: [{ stepId: 'loop-check', displayName: 'Loop check' }],
        queue: null,
        latestEventAt: stale,
      },
    }));
    fixture.detectChanges();

    const root = fixture.nativeElement.querySelector('[data-testid="task-live-status"]') as HTMLElement;
    expect(root.dataset['liveTone']).toBe('idle');
    expect(root.textContent).not.toContain('possible hang');
    expect(root.textContent).not.toContain('No active run');
    expect(root.textContent).toContain('Preparing');
    // last activity still surfaced, live-ticking via NowTickService
    expect(root.textContent).toMatch(/Last activity (?:92h59m|93h00m) ago/);
  });

  // AGT-3589 (walkthrough F14): idle Post Processing cards read "Between steps
  // · Last activity 0s ago" while nothing ran. They name the wait instead.
  it.each([
    { state: '4-auto-review', headline: 'Waiting for review' },
    { state: '3-progress', headline: 'Waiting for the next step' },
    { state: '2-ready', headline: 'Waiting for a runner' },
  ])('names what an idle $state card waits for', ({ state, headline }) => {
    const recent = new Date(Date.now() - 5 * 60_000).toISOString();
    fixture.componentRef.setInput('task', task({
      state,
      lastActivity: recent,
      liveStatus: { attempt: 1, activeStep: null, nextSteps: [], queue: null, latestEventAt: recent },
    }));
    fixture.detectChanges();

    const root = fixture.nativeElement.querySelector('[data-testid="task-live-status"]') as HTMLElement;
    expect(root.textContent).toContain(headline);
    expect(root.textContent).not.toContain('Between steps');
    expect(root.textContent).toMatch(/Last activity (?:4m59s|5m00s) ago/);
  });

  it('shows the real activity stamp instead of "0s ago" when it lies ahead of the clock', () => {
    const ahead = new Date(Date.now() + 3 * 60 * 60_000);
    fixture.componentRef.setInput('task', task({
      lastActivity: ahead.toISOString(),
      liveStatus: { attempt: 1, activeStep: null, nextSteps: [], queue: null, latestEventAt: ahead.toISOString() },
    }));
    fixture.detectChanges();

    const root = fixture.nativeElement.querySelector('[data-testid="task-live-status"]') as HTMLElement;
    const clock = ahead.toLocaleString('en-US', { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' });
    expect(root.textContent).toContain(`Last activity ${clock}`);
    expect(root.textContent).not.toContain('0s ago');
  });
});
