import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { PipelineHealthBlockComponent } from './pipeline-health-block';

describe('PipelineHealthBlockComponent', () => {
  let http: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [PipelineHealthBlockComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('renders hanging gate, systemic fingerprint, and stalled lane as one health alarm', async () => {
    const fixture = TestBed.createComponent(PipelineHealthBlockComponent);
    fixture.componentRef.setInput('projectName', 'Agent Taskboard');
    fixture.detectChanges();

    http.expectOne('/api/projects/Agent%20Taskboard/pipeline-health').flush({
      project: 'Agent Taskboard',
      capturedAtUtc: '2026-07-23T01:00:00Z',
      status: 'alarm',
      activeGate: {
        gateRunId: 'gate-1',
        project: 'Agent Taskboard',
        jobId: 'AGT-2183',
        acquiredAtUtc: '2026-07-22T22:30:00Z',
        elapsedMinutes: 150,
        budgetMinutes: 30,
        isHanging: true,
      },
      fingerprint: {
        fingerprint: 'lock:9c2f19e4a88c73ab',
        consecutiveFailures: 3,
        threshold: 3,
        projects: ['Agent Taskboard', 'Website'],
        isSystemic: true,
      },
      lanes: [
        { lane: '2-ready', queueCount: 1, completedPerHour: 1, isStalled: false },
        { lane: '4-auto-review', queueCount: 4, completedPerHour: 0, isStalled: true },
      ],
      alerts: [],
    });
    fixture.detectChanges();
    // AGT-3011: the operator sweeps render under the health alarm.
    http.expectOne('/api/projects/Agent%20Taskboard/operator-sweeps').flush({
      project: 'Agent Taskboard', capturedAtUtc: '2026-07-23T01:00:00Z', status: 'healthy', enabled: true,
      tickIntervalSeconds: 600, maxRoundsPerCard: 4, sweeps: [], cards: [], waitingForPerson: [],
    });
    // AGT-3070: one cross-project gate-economics fetch; this test does not need its rows.
    http.expectOne('/api/gate-economics?window=7d').flush({ window: '7d', asOfUtc: '2026-07-23T01:00:00Z', rows: [] });
    fixture.detectChanges();
    await fixture.whenStable();

    const host: HTMLElement = fixture.nativeElement;
    expect(host.querySelector('[data-testid="operator-sweeps"]')).not.toBeNull();
    const health = host.querySelector('[data-testid="pipeline-health"]');
    expect(health?.getAttribute('data-status')).toBe('alarm');
    expect(host.querySelector('[data-testid="pipeline-health-gate"]')?.textContent)
      .toContain('Gate hanging since 150 min');
    expect(host.querySelector('[data-testid="pipeline-health-fingerprint"]')?.textContent)
      .toContain('Systemic gate problem');
    const drain = host.querySelector('[data-testid="pipeline-health-drain"] [data-lane="4-auto-review"]');
    expect(drain?.textContent).toContain('0/h');
    expect(drain?.textContent).toContain('4 queued');
    expect(drain?.classList.contains('ph__drain--alarm')).toBe(true);
    expect(host.querySelector('[data-testid="pipeline-health-evidence-flush"]')).toBeNull();
  });

  it('shows a stalled workspace evidence flush with its repository and error (AGT-3000)', async () => {
    const fixture = TestBed.createComponent(PipelineHealthBlockComponent);
    fixture.componentRef.setInput('projectName', 'Agent Taskboard');
    fixture.detectChanges();

    http.expectOne('/api/projects/Agent%20Taskboard/pipeline-health').flush({
      project: 'Agent Taskboard',
      capturedAtUtc: '2026-09-27T15:50:00Z',
      status: 'alarm',
      activeGate: null,
      fingerprint: null,
      lanes: [],
      alerts: [{
        kind: 'evidence-flush-stalled',
        severity: 'high',
        summary: 'Workspace evidence flush failing for 17 min',
        detail: "Repository C:\\Projects\\agent-taskboard-workspace: 9 consecutive evidence flushes failed. "
          + "Last error: git-add: fatal: Unable to create '.git/index.lock': File exists.",
        detectedAtUtc: '2026-09-27T15:49:00Z',
        repository: 'C:\\Projects\\agent-taskboard-workspace',
      }],
    });
    fixture.detectChanges();
    http.expectOne('/api/projects/Agent%20Taskboard/operator-sweeps').flush({
      project: 'Agent Taskboard', capturedAtUtc: '2026-09-27T15:50:00Z', status: 'healthy', enabled: true,
      tickIntervalSeconds: 600, maxRoundsPerCard: 4, sweeps: [], cards: [], waitingForPerson: [],
    });
    http.expectOne('/api/gate-economics?window=7d').flush({ window: '7d', asOfUtc: '2026-09-27T15:50:00Z', rows: [] });
    fixture.detectChanges();
    await fixture.whenStable();

    const host: HTMLElement = fixture.nativeElement;
    expect(host.querySelector('[data-testid="pipeline-health"]')?.getAttribute('data-status')).toBe('alarm');
    const stall = host.querySelector('[data-testid="pipeline-health-evidence-flush"]');
    expect(stall?.getAttribute('data-repository')).toBe('C:\\Projects\\agent-taskboard-workspace');
    expect(stall?.textContent).toContain('Workspace evidence flush failing for 17 min');
    expect(stall?.textContent).toContain('index.lock');
  });

  it('reads its project row from the one gate-economics fetch and ignores a malformed payload (AGT-3070)', async () => {
    const fixture = TestBed.createComponent(PipelineHealthBlockComponent);
    fixture.componentRef.setInput('projectName', 'Agent Taskboard');
    fixture.detectChanges();

    http.expectOne('/api/projects/Agent%20Taskboard/pipeline-health').flush({
      project: 'Agent Taskboard', capturedAtUtc: '2026-10-13T08:00:00Z', status: 'healthy',
      activeGate: null, fingerprint: null, lanes: [], alerts: [],
    });
    http.expectOne('/api/projects/Agent%20Taskboard/operator-sweeps').flush({
      project: 'Agent Taskboard', capturedAtUtc: '2026-10-13T08:00:00Z', status: 'healthy', enabled: true,
      tickIntervalSeconds: 600, maxRoundsPerCard: 4, sweeps: [], cards: [], waitingForPerson: [],
    });
    const economics = http.match('/api/gate-economics?window=7d');
    expect(economics.length).toBe(1);
    economics[0].flush({});
    fixture.detectChanges();
    await fixture.whenStable();

    const host: HTMLElement = fixture.nativeElement;
    expect(host.querySelector('[data-testid="pipeline-health-economics"]')).toBeNull();
    expect(host.querySelector('[data-testid="gate-economics"]')?.textContent).toContain('Loading gate measurements');
  });

  it('shows the project row of the gate-economics projection in the health block (AGT-3070)', async () => {
    const fixture = TestBed.createComponent(PipelineHealthBlockComponent);
    fixture.componentRef.setInput('projectName', 'Agent Taskboard');
    fixture.detectChanges();

    http.expectOne('/api/projects/Agent%20Taskboard/pipeline-health').flush({
      project: 'Agent Taskboard', capturedAtUtc: '2026-10-13T08:00:00Z', status: 'healthy',
      activeGate: null, fingerprint: null, lanes: [], alerts: [],
    });
    http.expectOne('/api/projects/Agent%20Taskboard/operator-sweeps').flush({
      project: 'Agent Taskboard', capturedAtUtc: '2026-10-13T08:00:00Z', status: 'healthy', enabled: true,
      tickIntervalSeconds: 600, maxRoundsPerCard: 4, sweeps: [], cards: [], waitingForPerson: [],
    });
    const row = (project: string, gatedMerges: number) => ({
      project, gatedMerges, gateGreen: gatedMerges, gateRed: 0, gateCancelled: 0,
      gateMinutesMedian: 42, gateMinutesP90: 51, queueWaitP90Ms: 120_000, exactReuseRate: 0.25,
      disjointReuseRate: 0, fullSuiteShare: 0.75, sameShaRetestRate: null, lastNightlyGuard: null,
      deliveryLagP50Minutes: 30, deliveryLagP95Minutes: 90, oldestWaitingMinutes: null, preparationP50Minutes: null,
      queued: 2, prepared: 0, gating: 1, readyToPublish: 0, laneGateDurationP50Minutes: null,
      laneGateDurationP95Minutes: null, laneGateGreen: 0, laneGateRed: 0, laneGateCancelled: 0,
      gateTokenWaitP50Ms: null, gateTokenWaitP95Ms: null, gateHoldingProject: null, mergeLockWaitP95Ms: null,
      publicationLockWaitP95Ms: null, ejectedConflict: 0, ejectedAgentRound: 0, ejectedRed: 0, ejectedError: 0,
      arrivalsPerHour: 0, mergesPerHour: 0, projectShortCode: 'AGT', projectColor: null,
    });
    http.expectOne('/api/gate-economics?window=7d').flush({
      window: '7d', asOfUtc: '2026-10-13T08:00:00Z',
      rows: [row('All projects', 4), row('Agent Taskboard', 4)],
    });
    fixture.detectChanges();
    await fixture.whenStable();

    const economics = (fixture.nativeElement as HTMLElement).querySelector('[data-testid="pipeline-health-economics"]');
    expect(economics?.textContent).toContain('4 gated merges');
    expect(economics?.textContent).toContain('queue p90 2 min');
    expect(economics?.textContent).toContain('Last 24 h: integration lag p95 90 min · 2 queued · 1 gating');
  });
});
