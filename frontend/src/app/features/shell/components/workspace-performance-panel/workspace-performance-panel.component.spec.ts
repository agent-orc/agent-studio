import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { WorkspacePerformancePanelComponent } from './workspace-performance-panel.component';

const REPORT = {
  windowStartUtc: '2026-09-07T08:00:00Z',
  windowEndUtc: '2026-09-07T09:00:00Z',
  endpoints: [
    { label: 'tasks/grouped', calls: 351, p50Ms: 4, p95Ms: 12, spawns: 0, gitMs: 0 },
    { label: 'tasks/list-refresh', calls: 12, p50Ms: 900, p95Ms: 1800, spawns: 96, gitMs: 14000 },
  ],
  repositories: [
    {
      repositoryRoot: '/repos/agent-studio',
      projectNames: ['agent-studio'],
      gitStateAt: '2026-09-07T08:59:55Z',
      ageSeconds: 5,
      lastTrigger: 'ref-change',
      lastRunMs: 640,
      lastRunSpawns: 8,
      refreshing: false,
    },
  ],
  recentRuns: [],
  spawns: 96,
  spawnsPerMinute: 1.6,
  warnings: [{ code: 'spawn-budget', message: 'Git spawns are running at 45 per minute; the budget is 20 per minute.' }],
};

describe('WorkspacePerformancePanelComponent', () => {
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [WorkspacePerformancePanelComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    }).compileComponents();
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('reports the spawn rate, per-endpoint percentiles, and each repository index age', async () => {
    const fixture = TestBed.createComponent(WorkspacePerformancePanelComponent);
    fixture.detectChanges();
    httpMock.expectOne('/api/admin/performance/git-state').flush(REPORT);
    fixture.detectChanges();
    await fixture.whenStable();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="workspace-performance-spawn-rate"]')?.textContent)
      .toContain('1.6');
    const endpoints = host.querySelector('[data-testid="workspace-performance-endpoints"]')!;
    expect(endpoints.textContent).toContain('tasks/grouped');
    expect(endpoints.textContent).toContain('12 ms');
    const repositories = host.querySelector('[data-testid="workspace-performance-repositories"]')!;
    expect(repositories.textContent).toContain('/repos/agent-studio');
    expect(repositories.textContent).toContain('5 s ago');
    expect(host.querySelector('[data-testid="workspace-performance-warnings"]')?.textContent)
      .toContain('the budget is 20 per minute');
  });

  it('names a failed read instead of rendering an empty panel', async () => {
    const fixture = TestBed.createComponent(WorkspacePerformancePanelComponent);
    fixture.detectChanges();
    httpMock.expectOne('/api/admin/performance/git-state')
      .flush('boom', { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();
    await fixture.whenStable();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="workspace-performance-error"]')?.textContent)
      .toContain('could not be read');
  });
});
