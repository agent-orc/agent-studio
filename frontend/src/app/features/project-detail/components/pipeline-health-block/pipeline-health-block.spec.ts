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
    await fixture.whenStable();

    const host: HTMLElement = fixture.nativeElement;
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
    await fixture.whenStable();

    const host: HTMLElement = fixture.nativeElement;
    expect(host.querySelector('[data-testid="pipeline-health"]')?.getAttribute('data-status')).toBe('alarm');
    const stall = host.querySelector('[data-testid="pipeline-health-evidence-flush"]');
    expect(stall?.getAttribute('data-repository')).toBe('C:\\Projects\\agent-taskboard-workspace');
    expect(stall?.textContent).toContain('Workspace evidence flush failing for 17 min');
    expect(stall?.textContent).toContain('index.lock');
  });
});
