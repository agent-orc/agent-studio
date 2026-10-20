import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ProjectOperationsComponent } from './project-operations';
import type { ProjectOperations } from './project-operations.model';

const snapshot: ProjectOperations = {
  project: 'demo', since: '2026-09-20T00:00:00Z', until: '2026-10-20T00:00:00Z',
  environments: [{ environment: { id: 'prod', name: 'Production', kind: 'production' },
    telemetryAvailable: false, metrics: null,
    markers: [{ deploymentId: 'run-1', releaseId: 'rel-1', version: '2026.10.20.1',
      atUtc: '2026-10-20T00:00:00Z', trigger: 'rollback', state: 'succeeded', cardKeys: ['CARD-1'] }] }],
  dora: { successfulProductionDeploys: 2, deploysPerWeek: 0.5, medianCardToProductionHours: 12,
    cardsMeasured: 2, changeFailureRate: 0.25, medianRecoveryHours: 3, recoveriesMeasured: 1 },
};

describe('ProjectOperationsComponent', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectOperationsComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
  });

  it('shows unavailable telemetry without invented numbers and links a rollback to release and card', async () => {
    const fixture = TestBed.createComponent(ProjectOperationsComponent);
    fixture.componentRef.setInput('projectName', 'demo');
    fixture.detectChanges();
    TestBed.inject(HttpTestingController).expectOne('/api/projects/demo/deploy/operations').flush(snapshot);
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;
    expect(host.textContent).toContain('Telemetry is not available yet');
    expect(host.textContent).toContain('rollback · succeeded');
    expect(host.textContent).toContain('0.5 per week');
    expect(host.textContent).toContain('25%');
    const releases: string[] = [];
    const cards: string[] = [];
    fixture.componentInstance.openRelease.subscribe(id => releases.push(id));
    fixture.componentInstance.openCard.subscribe(id => cards.push(id));
    host.querySelector<HTMLButtonElement>('[data-testid="operations-release-rel-1"]')!.click();
    [...host.querySelectorAll<HTMLButtonElement>('.project-operations__markers button')][1].click();
    expect(releases).toEqual(['rel-1']);
    expect(cards).toEqual(['CARD-1']);
  });

  it('passes environment, time, level, release and text filters to bounded log search', async () => {
    const fixture = TestBed.createComponent(ProjectOperationsComponent);
    fixture.componentRef.setInput('projectName', 'demo');
    fixture.detectChanges();
    const requests = TestBed.inject(HttpTestingController);
    requests.expectOne('/api/projects/demo/deploy/operations').flush(snapshot);
    await fixture.whenStable();
    const view = fixture.componentInstance;
    view.since.set('2026-10-19T10:00'); view.until.set('2026-10-20T10:00');
    view.level.set('error'); view.release.set('rel-1'); view.searchText.set('timeout');
    view.search();
    const request = requests.expectOne(req => req.url.endsWith('/deploy/operations/logs'));
    expect(request.request.params.get('environmentId')).toBe('prod');
    expect(request.request.params.get('since')).toContain('2026-10-19');
    expect(request.request.params.get('until')).toContain('2026-10-20');
    expect(request.request.params.get('level')).toBe('error');
    expect(request.request.params.get('releaseId')).toBe('rel-1');
    expect(request.request.params.get('text')).toBe('timeout');
    request.flush({ telemetryAvailable: false, lines: [] });
    await fixture.whenStable();
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="operations-logs-empty"]')).not.toBeNull();
  });

  it('clears shown logs on environment and filter changes and ignores an in-flight older search', async () => {
    const fixture = TestBed.createComponent(ProjectOperationsComponent);
    fixture.componentRef.setInput('projectName', 'demo');
    fixture.detectChanges();
    const requests = TestBed.inject(HttpTestingController);
    requests.expectOne('/api/projects/demo/deploy/operations').flush({ ...snapshot, environments: [
      ...snapshot.environments, { ...snapshot.environments[0], environment: { id: 'stage', name: 'Staging', kind: 'staging' } },
    ] });
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;
    const view = fixture.componentInstance;
    const result = { telemetryAvailable: true, lines: [{ at: '2026-10-20T00:00:00Z', level: 'error',
      message: 'Old production error', releaseId: null }] };
    view.search();
    requests.expectOne(req => req.url.endsWith('/deploy/operations/logs')).flush(result);
    await fixture.whenStable();
    expect(host.textContent).toContain('Old production error');

    const environment = host.querySelector<HTMLSelectElement>('[data-testid="operations-log-environment"]')!;
    environment.value = 'stage'; environment.dispatchEvent(new Event('change'));
    await fixture.whenStable();
    expect(host.textContent).not.toContain('Old production error');
    view.search();
    const oldRequest = requests.expectOne(req => req.url.endsWith('/deploy/operations/logs'));
    const text = host.querySelector<HTMLInputElement>('input[type="search"]')!;
    text.value = 'new'; text.dispatchEvent(new Event('input'));
    oldRequest.flush(result);
    await fixture.whenStable();
    expect(host.textContent).not.toContain('Old production error');

    view.search();
    const current = requests.expectOne(req => req.url.endsWith('/deploy/operations/logs'));
    expect(current.request.params.get('environmentId')).toBe('stage');
    expect(current.request.params.get('text')).toBe('new');
    current.flush({ telemetryAvailable: true, lines: [{ ...result.lines[0], message: 'New stage error' }] });
    await fixture.whenStable();
    expect(host.textContent).toContain('New stage error');
  });

  it('renders telemetry measurements, daily anonymous usage and marked trend curves', async () => {
    const fixture = TestBed.createComponent(ProjectOperationsComponent);
    fixture.componentRef.setInput('projectName', 'demo');
    fixture.detectChanges();
    const metrics = {
      responseP50Ms: 20, responseP95Ms: 80, errorRate: 0.02, restarts: 1,
      requests: 500, sessions: 80, activeUsers: 50,
      cpuCores: 1, cpuReservedCores: 2,
      memoryBytes: 1024, memoryReservedBytes: 2048,
      diskBytes: 4096, diskReservedBytes: 8192, networkBytes: 5000,
      topEndpoints: [{ path: '/health', requests: 120 }],
      dailyUsage: [{ day: '2026-10-20', requests: 500, sessions: 80, activeUsers: 50 }],
      samples: [{ at: '2026-10-19T00:00:00Z', responseP95Ms: 80, errorRate: 0.02,
        requests: 500, cpuCores: 1, memoryBytes: 1024, diskBytes: 4096 }],
    };
    TestBed.inject(HttpTestingController).expectOne('/api/projects/demo/deploy/operations')
      .flush({ ...snapshot, environments: [{ ...snapshot.environments[0], telemetryAvailable: true, metrics }] });
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;
    expect(host.textContent).toContain('Response p95');
    expect(host.textContent).toContain('80 ms');
    expect(host.textContent).toContain('1 cores / 2 cores');
    expect(host.textContent).toContain('Daily usage');
    expect(host.textContent).toContain('/health');
    expect(host.querySelectorAll('.trend__marker').length).toBe(6);
    const releases: string[] = [];
    fixture.componentInstance.openRelease.subscribe(id => releases.push(id));
    host.querySelector<SVGGElement>('.trend__marker-link')!.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    expect(releases).toEqual(['rel-1']);
  });
});
