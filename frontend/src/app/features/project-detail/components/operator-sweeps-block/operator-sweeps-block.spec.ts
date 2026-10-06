import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import type { OperatorSweepProjection } from '../../../task-pipeline';
import { OperatorSweepsBlockComponent } from './operator-sweeps-block';

function projection(overrides: Partial<OperatorSweepProjection> = {}): OperatorSweepProjection {
  return {
    project: 'Agent Taskboard',
    capturedAtUtc: '2026-10-04T12:50:00Z',
    status: 'alarm',
    enabled: true,
    tickIntervalSeconds: 600,
    maxRoundsPerCard: 4,
    lastTickAtUtc: '2026-10-04T12:48:00Z',
    sweeps: [
      {
        sweep: 'fix-rounds', paused: false, isOverdue: false, lastActed: 1, lastHeld: 2, lastWaitingForPerson: 1,
        lastRunStartedAtUtc: '2026-10-04T12:47:00Z', lastRunFinishedAtUtc: '2026-10-04T12:48:00Z', recentActions: [],
      },
      {
        sweep: 'gate-triage', paused: true, pausedBy: 'human:ops', pauseReason: 'gate host repair',
        isOverdue: false, lastActed: 0, lastHeld: 0, lastWaitingForPerson: 0, recentActions: [],
      },
      {
        sweep: 'salvage', paused: false, isOverdue: true, lastActed: 0, lastHeld: 0, lastWaitingForPerson: 0,
        recentActions: [],
      },
    ],
    cards: [
      {
        taskKey: 'AGT-2955', jobId: 'AGT-2955', title: 'Budget card', lane: '5-human-review',
        roundsUsed: 4, roundsAllowed: 4, roundsRemaining: 0,
        decisions: [{
          sweep: 'fix-rounds', action: 'WaitForPerson', reason: 'round-budget-exhausted',
          detail: 'The card has used its whole round budget; a person decides the next step.',
        }],
      },
      {
        taskKey: 'AGT-3001', jobId: 'AGT-3001', title: 'Fresh failure', lane: '5-human-review',
        roundsUsed: 1, roundsAllowed: 4, roundsRemaining: 3,
        decisions: [{ sweep: 'fix-rounds', action: 'Act', reason: 'fresh-product-failure', detail: 'Fix round opened.' }],
      },
    ],
    waitingForPerson: [{
      taskKey: 'AGT-2955', title: 'Budget card', lane: '5-human-review', sweep: 'fix-rounds',
      reason: 'round-budget-exhausted',
      detail: 'The card has used its whole round budget; a person decides the next step.',
    }],
    ...overrides,
  };
}

describe('OperatorSweepsBlockComponent', () => {
  let http: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [OperatorSweepsBlockComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  async function render() {
    const fixture = TestBed.createComponent(OperatorSweepsBlockComponent);
    fixture.componentRef.setInput('projectName', 'Agent Taskboard');
    fixture.detectChanges();
    http.expectOne('/api/projects/Agent%20Taskboard/operator-sweeps').flush(projection());
    fixture.detectChanges();
    await fixture.whenStable();
    return fixture;
  }

  it('renders run state, pause, overdue, waiting cards, and rounds left per card', async () => {
    const fixture = await render();
    const host: HTMLElement = fixture.nativeElement;

    expect(host.querySelector('[data-testid="operator-sweeps"]')?.getAttribute('data-status')).toBe('alarm');
    expect(host.querySelector('[data-testid="operator-sweep-fix-rounds"]')?.textContent)
      .toContain('1 acted · 2 held · 1 waiting');
    const gate = host.querySelector('[data-testid="operator-sweep-gate-triage"]');
    expect(gate?.textContent).toContain('Paused');
    expect(gate?.textContent).toContain('gate host repair');
    expect(host.querySelector('[data-testid="operator-sweep-toggle-gate-triage"]')?.textContent?.trim()).toBe('Resume');
    expect(host.querySelector('[data-testid="operator-sweep-salvage"]')?.textContent).toContain('Overdue');

    const waiting = host.querySelector('[data-testid="operator-sweeps-waiting"] [data-task="AGT-2955"]');
    expect(waiting?.textContent).toContain('whole round budget');
    const budget = host.querySelector('[data-testid="operator-sweeps-budget"] [data-task="AGT-3001"]');
    expect(budget?.textContent).toContain('3 of 4 left');
    expect(host.querySelector('[data-testid="operator-sweeps-budget"] [data-task="AGT-2955"] .os__rounds--spent'))
      .not.toBeNull();
  });

  it('pauses a sweep through the API and shows the returned state', async () => {
    const fixture = await render();
    const host: HTMLElement = fixture.nativeElement;

    (host.querySelector('[data-testid="operator-sweep-toggle-fix-rounds"]') as HTMLButtonElement).click();
    const request = http.expectOne('/api/projects/Agent%20Taskboard/operator-sweeps/fix-rounds/pause');
    expect(request.request.method).toBe('POST');
    const paused = projection();
    paused.sweeps[0] = { ...paused.sweeps[0], paused: true, pausedBy: 'human:ops' };
    request.flush(paused);
    fixture.detectChanges();
    await fixture.whenStable();

    expect(host.querySelector('[data-testid="operator-sweep-toggle-fix-rounds"]')?.textContent?.trim()).toBe('Resume');
    expect(host.querySelector('[data-testid="operator-sweep-fix-rounds"]')?.textContent).toContain('human:ops');
  });
});
