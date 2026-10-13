import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { ProjectCycleTimePanelComponent } from './project-cycle-time-panel.component';
import type { ProjectCycleTimeResponse } from '../../models/project-cycle-time.model';

/**
 * Renders the panel against a flushed HTTP response and asserts the calm
 * surface: window selector, summary line, composition bar segments in lane
 * order, the stage table with highlighted rows, and the sortable drill-down.
 */
describe('ProjectCycleTimePanelComponent', () => {
  async function mount() {
    await TestBed.configureTestingModule({
      imports: [ProjectCycleTimePanelComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(ProjectCycleTimePanelComponent);
    fixture.componentRef.setInput('projectName', 'Demo');
    const http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    await fixture.whenStable();
    return { fixture, http };
  }

  it('requests the 7d window by default and renders aggregates, bar, and tasks', async () => {
    const { fixture, http } = await mount();
    const request = http.expectOne(r => r.url === '/api/projects/Demo/cycle-time');
    expect(request.request.params.get('window')).toBe('7d');
    request.flush(response());
    fixture.detectChanges();
    await fixture.whenStable();

    const root: HTMLElement = fixture.nativeElement;
    expect(root.querySelector('[data-testid="cycle-time-summary"]')?.textContent).toContain('2 tasks completed');
    expect(root.querySelector('[data-testid="cycle-time-window-7d"]')?.getAttribute('aria-pressed')).toBe('true');
    expect(root.querySelector('[data-testid="flow-overview"]')).not.toBeNull();
    (root.querySelector('[data-testid="cycle-time-lane-toggle"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(fixture.componentInstance.laneStages()).toBe(true);
    expect(root.querySelector('[data-testid="cycle-time-bar"]')).not.toBeNull();

    const segments = Array.from(root.querySelectorAll('[data-testid="cycle-time-bar"] .cyc__segment'))
      .map(el => el.getAttribute('data-stage'));
    expect(segments).toEqual(['queueWait', 'coding', 'testGate', 'integration', 'humanReview']);

    const stageRows = Array.from(root.querySelectorAll('[data-testid="cycle-time-stages"] tbody:not(.cyc__rollups) tr'))
      .map(el => el.getAttribute('data-stage'));
    expect(stageRows).toEqual(['queueWait', 'coding', 'testGate', 'integration', 'humanReview']);
    const gateRow = root.querySelector('[data-testid="cycle-time-stages"] tr[data-stage="testGate"]');
    expect(gateRow?.classList.contains('cyc__row--highlighted')).toBe(true);
    expect(gateRow?.textContent).toContain('10m');

    const rollups = Array.from(root.querySelectorAll('[data-testid="cycle-time-stages"] .cyc__rollups tr'))
      .map(el => el.getAttribute('data-stage'));
    expect(rollups).toEqual(['reviewRun', 'leadTime', 'cycleTime']);

    const keys = () => Array.from(root.querySelectorAll('[data-testid="cycle-time-tasks"] tbody tr'))
      .map(el => el.getAttribute('data-task-key'));
    expect(keys()).toEqual(['DEM-2', 'DEM-1']);

    (root.querySelector('[data-testid="cycle-time-sort-testGate"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(keys()).toEqual(['DEM-1', 'DEM-2']);
    expect(root.querySelector('[data-testid="cycle-time-outcomes"]')?.textContent).toContain('Merged 1');

    // Transition section: bounce causes, matrix with backward marker, dwell, loops.
    expect(root.querySelector('[data-testid="cycle-time-transitions-summary"]')?.textContent).toContain('9 lane moves');
    const causeRows = Array.from(root.querySelectorAll('[data-testid="cycle-time-bounce-causes"] tbody tr'))
      .map(el => el.getAttribute('data-cause'));
    expect(causeRows).toEqual(['gate-failure', 'operator-requeue']);
    expect(root.querySelector('[data-testid="cycle-time-bounce-causes"] tr[data-cause="gate-failure"]')?.textContent)
      .toContain('15m');
    const backwardCell = root.querySelector('[data-testid="cycle-time-matrix"] tr[data-from="4-auto-review"] td[data-to="2-ready"]');
    expect(backwardCell?.classList.contains('ctt__cell--backward')).toBe(true);
    expect(backwardCell?.textContent).toContain('2');
    const readyRow = root.querySelector('[data-testid="cycle-time-matrix"] tr[data-from="2-ready"]');
    expect(readyRow?.querySelector('td[data-to="3-progress"]')?.getAttribute('data-count')).toBe('4');
    expect(Array.from(root.querySelectorAll('[data-testid="cycle-time-lane-dwell"] tbody tr')).map(el => el.getAttribute('data-lane')))
      .toEqual(['2-ready', '4-auto-review']);
    expect(root.querySelector('[data-testid="cycle-time-top-loops"] tr[data-task-key="DEM-1"]')?.textContent).toContain('gate-failure 2');
    http.verify();
  });

  it('expands a drill-down row and loads the task transition history on demand', async () => {
    const { fixture, http } = await mount();
    http.expectOne(r => r.url === '/api/projects/Demo/cycle-time').flush(response());
    fixture.detectChanges();
    await fixture.whenStable();
    const root: HTMLElement = fixture.nativeElement;

    const toggle = root.querySelector('[data-testid="cycle-time-expand-DEM-1"]') as HTMLButtonElement;
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    toggle.click();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(toggle.getAttribute('aria-expanded')).toBe('true');

    const request = http.expectOne('/api/projects/Demo/cycle-time/tasks/DEM-1');
    request.flush({
      project: 'Demo',
      capturedAt: '2026-08-22T12:00:00Z',
      task: { ...response().tasks[1], transitions: transitionsOf('DEM-1') },
    });
    fixture.detectChanges();
    await fixture.whenStable();

    const rows = Array.from(root.querySelectorAll('[data-testid="cycle-time-task-transitions-DEM-1"] tbody tr'));
    expect(rows.map(r => r.getAttribute('data-cause'))).toEqual(['claimed', 'delivered', 'gate-failure', 'claimed', 'delivered', 'review-verdict', 'accepted']);
    expect(rows[2].classList.contains('ctx__row--backward')).toBe(true);
    expect(rows[2].textContent).toContain('build-test-gate-fail');
    expect(rows[2].textContent).toContain('20m'); // rework

    toggle.click();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector('[data-testid="cycle-time-task-transitions-DEM-1"]')).toBeNull();
    http.verify();
  });

  it('switches the window, emits openTask for a drill-down row, and shows load errors', async () => {
    const { fixture, http } = await mount();
    http.expectOne(r => r.url === '/api/projects/Demo/cycle-time').flush(response());
    fixture.detectChanges();
    await fixture.whenStable();

    const opened: { jobId: string; watchPath: string }[] = [];
    fixture.componentInstance.openTask.subscribe(value => opened.push(value));
    const root: HTMLElement = fixture.nativeElement;
    (root.querySelector('[data-testid="cycle-time-open-DEM-1"]') as HTMLButtonElement).click();
    expect(opened).toEqual([{ jobId: 'dem-1', watchPath: 'C:/tasks/demo' }]);

    (root.querySelector('[data-testid="cycle-time-window-30d"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    await fixture.whenStable();
    const second = http.expectOne(r => r.url === '/api/projects/Demo/cycle-time');
    expect(second.request.params.get('window')).toBe('30d');
    second.flush({ error: 'boom' }, { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector('[data-testid="cycle-time-error"]')?.textContent).toContain('boom');
    http.verify();
  });

  it('round trips the 24 hour window through the existing request', async () => {
    const { fixture, http } = await mount();
    http.expectOne(r => r.url === '/api/projects/Demo/cycle-time').flush(response());
    fixture.detectChanges();
    await fixture.whenStable();
    const root: HTMLElement = fixture.nativeElement;
    (root.querySelector('[data-testid="cycle-time-window-1d"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    await fixture.whenStable();
    const request = http.expectOne(r => r.url === '/api/projects/Demo/cycle-time');
    expect(request.request.params.get('window')).toBe('1d');
    request.flush({ ...response(), window: '1d' });
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector('[data-testid="cycle-time-summary"]')?.textContent).toContain('Last 24 hours');
    http.verify();
  });

  it('drops a late response of a superseded window request', async () => {
    const { fixture, http } = await mount();
    const first = http.expectOne(r => r.url === '/api/projects/Demo/cycle-time');
    const root: HTMLElement = fixture.nativeElement;

    // Switch to 30d before the 7d response arrives, then let the 30d response land first.
    (root.querySelector('[data-testid="cycle-time-window-30d"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    await fixture.whenStable();
    const second = http.expectOne(r => r.url === '/api/projects/Demo/cycle-time' && r.params.get('window') === '30d');
    second.flush({ ...response(), window: '30d', coverage: { ...response().coverage, tasksInWindow: 9 } });
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector('[data-testid="cycle-time-summary"]')?.textContent).toContain('9 tasks completed');

    first.flush(response());
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector('[data-testid="cycle-time-summary"]')?.textContent).toContain('9 tasks completed');
    expect(fixture.componentInstance.loading()).toBe(false);
    http.verify();
  });
});

function response(): ProjectCycleTimeResponse {
  const base = {
    terminalState: '7-archive',
    watchPath: 'C:/tasks/demo',
    createdAt: '2026-08-18T00:00:00Z',
    firstClaimedAt: '2026-08-18T00:10:00Z',
    completionSource: 'ledger',
    reviewRounds: 1,
    bounceRounds: 0,
    integrationAttempts: 1,
    integrationStage: 'pre-human-review',
    dataGaps: [] as string[],
  };
  return {
    project: 'Demo',
    projectId: 'PROJ-001',
    shortCode: 'DEM',
    window: '7d',
    capturedAt: '2026-08-22T12:00:00Z',
    since: '2026-08-15T12:00:00Z',
    coverage: {
      tasksInProject: 5,
      tasksTerminal: 4,
      tasksInWindow: 2,
      excludedNoCompletionTimestamp: 1,
      excludedInFlight: 1,
      excludedEpics: 0,
      tasksWithoutLedger: 0,
      tasksWithLaneEntryCompletion: 0,
      tasksBackfilled: 0,
    },
    aggregates: [
      agg('preparation', 'Preparation', 'stage', 0, null),
      agg('queueWait', 'Queue wait', 'stage', 2, 300),
      agg('coding', 'Coding run', 'stage', 2, 1800),
      agg('reviewWait', 'Post-processing wait', 'stage', 0, null),
      agg('testGate', 'Build/test gate', 'stage', 2, 600, true),
      agg('reviewOther', 'Review aspects and decision', 'stage', 0, null),
      agg('integration', 'Integration', 'stage', 1, 120, true),
      agg('humanReview', 'Human review', 'stage', 2, 7200),
      agg('unattributed', 'Unattributed', 'stage', 0, null),
      agg('reviewRun', 'Review run', 'rollup', 2, 720),
      agg('leadTime', 'Lead time', 'rollup', 2, 10_000),
      agg('cycleTime', 'Cycle time', 'rollup', 2, 9700),
      agg('codingRuns', 'Coding runs', 'count', 2, 1, false, 'count'),
      agg('reviewRounds', 'Review rounds', 'count', 2, 1, false, 'count'),
      agg('bounceRounds', 'Bounce rounds', 'count', 2, 0, false, 'count'),
      agg('integrationAttempts', 'Integration attempts', 'count', 2, 1, false, 'count'),
    ],
    integrationOutcomes: [{ outcome: 'Merged', count: 1 }, { outcome: 'none', count: 1 }],
    transitions: {
      totalTransitions: 9,
      backwardTransitions: 3,
      tasksWithBackwardTransitions: 2,
      lanes: ['2-ready', '3-progress', '4-auto-review', '5-human-review', '6-completed'],
      cells: [
        { from: '2-ready', to: '3-progress', count: 4, direction: 'forward' },
        { from: '3-progress', to: '4-auto-review', count: 3, direction: 'forward' },
        { from: '4-auto-review', to: '2-ready', count: 2, direction: 'backward' },
        { from: '4-auto-review', to: '5-human-review', count: 1, direction: 'forward' },
        { from: '5-human-review', to: '2-ready', count: 1, direction: 'backward' },
        { from: '5-human-review', to: '6-completed', count: 1, direction: 'forward' },
      ],
      laneDwell: [
        { lane: '2-ready', stays: 4, p50Seconds: 120, p90Seconds: 600, maxSeconds: 600, totalSeconds: 1440 },
        { lane: '4-auto-review', stays: 3, p50Seconds: 600, p90Seconds: 900, maxSeconds: 900, totalSeconds: 2100 },
      ],
      bounceCauses: [
        { cause: 'gate-failure', label: 'Build/test gate failed', count: 2, tasks: 1, reworkKnown: 2, reworkP50Seconds: 900, reworkP90Seconds: 1200, reworkTotalSeconds: 2100, details: [{ outcome: 'build-test-gate-fail', count: 2 }] },
        { cause: 'operator-requeue', label: 'Operator requeue', count: 1, tasks: 1, reworkKnown: 0, reworkP50Seconds: null, reworkP90Seconds: null, reworkTotalSeconds: 0, details: [] },
      ],
      topLoops: [
        { taskId: 'dem-1', taskKey: 'DEM-1', title: 'First task', watchPath: 'C:/tasks/demo', backwardTransitions: 2, leadTimeSeconds: 10_320, causes: [{ outcome: 'gate-failure', count: 2 }] },
        { taskId: 'dem-2', taskKey: 'DEM-2', title: 'Second task', watchPath: 'C:/tasks/demo', backwardTransitions: 1, leadTimeSeconds: 9600, causes: [{ outcome: 'operator-requeue', count: 1 }] },
      ],
    },
    tasks: [
      {
        ...base,
        taskId: 'dem-2',
        taskKey: 'DEM-2',
        title: 'Second task',
        completedAt: '2026-08-21T10:00:00Z',
        stages: { preparation: 0, queueWait: 300, coding: 1800, reviewWait: 0, testGate: 300, reviewOther: 0, integration: 0, humanReview: 7200, unattributed: 0 },
        reviewRunSeconds: 300,
        leadTimeSeconds: 9600,
        cycleTimeSeconds: 9300,
        codingRuns: 1,
        integrationOutcome: null,
        backwardTransitions: 1,
      },
      {
        ...base,
        taskId: 'dem-1',
        taskKey: 'DEM-1',
        title: 'First task',
        completedAt: '2026-08-20T10:00:00Z',
        stages: { preparation: 0, queueWait: 300, coding: 1800, reviewWait: 0, testGate: 900, reviewOther: 0, integration: 120, humanReview: 7200, unattributed: 0 },
        reviewRunSeconds: 1020,
        leadTimeSeconds: 10_320,
        cycleTimeSeconds: 10_000,
        codingRuns: 1,
        integrationOutcome: 'Merged',
        backwardTransitions: 2,
      },
    ],
  };
}

function transitionsOf(key: string) {
  const t = (at: string, from: string, to: string, direction: 'forward' | 'backward' | 'lateral', cause: string, dwell: number, detail: string | null = null, rework: number | null = null) => ({
    at, from, to, direction, dwellSeconds: dwell, actor: direction === 'backward' ? 'system' : 'remote-runner:r', actorKind: direction === 'backward' ? 'system' : 'runner',
    cause, causeDetail: detail, attemptId: null, reworkSeconds: rework,
  });
  void key;
  return [
    t('2026-08-18T08:10:00Z', '2-ready', '3-progress', 'forward', 'claimed', 600),
    t('2026-08-18T08:40:00Z', '3-progress', '4-auto-review', 'forward', 'delivered', 1800),
    t('2026-08-18T08:50:00Z', '4-auto-review', '2-ready', 'backward', 'gate-failure', 600, 'build-test-gate-fail', 1200),
    t('2026-08-18T08:51:00Z', '2-ready', '3-progress', 'forward', 'claimed', 60),
    t('2026-08-18T09:10:00Z', '3-progress', '4-auto-review', 'forward', 'delivered', 1140),
    t('2026-08-18T09:20:00Z', '4-auto-review', '5-human-review', 'forward', 'review-verdict', 600, 'Merged'),
    t('2026-08-20T10:00:00Z', '5-human-review', '6-completed', 'forward', 'accepted', 175_200),
  ];
}

function agg(
  stage: string,
  label: string,
  kind: 'stage' | 'rollup' | 'count',
  count: number,
  p50: number | null,
  highlighted = false,
  unit: 'seconds' | 'count' = 'seconds',
) {
  return { stage, label, kind, unit, highlighted, count, p50, p90: p50, max: p50, mean: p50, total: (p50 ?? 0) * count };
}
