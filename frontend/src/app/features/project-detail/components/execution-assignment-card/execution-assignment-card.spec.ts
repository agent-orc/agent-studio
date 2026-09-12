import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ExecutionAssignmentCardComponent } from './execution-assignment-card';
import { RemoteHostsService } from '../../../remote-hosts';

describe('ExecutionAssignmentCardComponent', () => {
  it('loads and persists the project-dedicated host assignment', () => {
    TestBed.configureTestingModule({
      imports: [ExecutionAssignmentCardComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    });
    const fixture = TestBed.createComponent(ExecutionAssignmentCardComponent);
    fixture.componentRef.setInput('projectName', 'Agent Studio');
    fixture.detectChanges();

    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/projects/settings').flush({
      'Agent Studio': { pickupMode: 'manual', executionLocation: 'local', integrationBranch: 'develop' },
    });
    flushHostRegistryFailure(http);

    fixture.componentInstance.assign('agent-runner-01');
    const request = http.expectOne('/api/projects/Agent%20Studio/execution-runner');
    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({ executionLocation: 'agent-runner-01' });
    request.flush({ pickupMode: 'manual', executionLocation: 'agent-runner-01' });

    expect(fixture.componentInstance.selectedHostId()).toBe('agent-runner-01');
    http.verify();
  });

  it('persists local as the canonical execution location', () => {
    TestBed.configureTestingModule({
      imports: [ExecutionAssignmentCardComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    });
    const fixture = TestBed.createComponent(ExecutionAssignmentCardComponent);
    fixture.componentRef.setInput('projectName', 'Agent Studio');
    fixture.detectChanges();

    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/projects/settings').flush({
      'Agent Studio': { pickupMode: 'auto', executionLocation: 'agent-runner-01' },
    });
    flushHostRegistryFailure(http);

    fixture.componentInstance.assign('local');
    const request = http.expectOne('/api/projects/Agent%20Studio/execution-runner');
    expect(request.request.body).toEqual({ executionLocation: 'local' });
    request.flush({ pickupMode: 'auto', executionLocation: 'local' });

    expect(fixture.componentInstance.selectedHostId()).toBe('local');
    http.verify();
  });

  it('changes pickup mode without changing a remote execution location', () => {
    TestBed.configureTestingModule({
      imports: [ExecutionAssignmentCardComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    });
    const fixture = TestBed.createComponent(ExecutionAssignmentCardComponent);
    fixture.componentRef.setInput('projectName', 'Agent Studio');
    fixture.detectChanges();

    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/projects/settings').flush({
      'Agent Studio': { pickupMode: 'auto', executionLocation: 'agent-runner-01' },
    });
    flushHostRegistryFailure(http);

    fixture.componentInstance.setPickupMode('paused');
    const request = http.expectOne('/api/projects/Agent%20Studio/execution-runner');
    expect(request.request.body).toEqual({ pickupMode: 'paused' });
    request.flush({ pickupMode: 'paused', executionLocation: 'agent-runner-01' });

    expect(fixture.componentInstance.pickupMode()).toBe('paused');
    expect(fixture.componentInstance.selectedHostId()).toBe('agent-runner-01');
    http.verify();
  });

  it('reports all four readiness checks independently', async () => {
    vi.useFakeTimers();
    try {
      TestBed.configureTestingModule({
        imports: [ExecutionAssignmentCardComponent],
        providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
      });
      const fixture = TestBed.createComponent(ExecutionAssignmentCardComponent);
      fixture.componentRef.setInput('projectName', 'Agent Studio');
      fixture.detectChanges();

      const http = TestBed.inject(HttpTestingController);
      http.expectOne('/api/projects/settings').flush({
        'Agent Studio': { pickupMode: 'auto', executionLocation: 'agent-runner-01', integrationBranch: 'develop' },
      });
      flushHostRegistryFailure(http);
      fixture.componentInstance.hostRegistry.hosts.update((hosts) =>
        hosts.map((host) => host.id === 'agent-runner-01'
          ? {
              ...host,
              status: 'online',
              capabilities: [...host.capabilities, 'node 22'],
              cliQuotas: [{
                cliType: 'codex',
                plan: null,
                windowLabel: 'weekly',
                usedPct: 0,
                resetLabel: null,
              }],
            }
          : host),
      );

      const probe = fixture.componentInstance.runProbe();
      await vi.runAllTimersAsync();
      await probe;

      expect(fixture.componentInstance.checks().map((check) => [check.key, check.state])).toEqual([
        ['code', 'passed'],
        ['branch', 'passed'],
        ['toolchain', 'passed'],
        ['noop', 'passed'],
      ]);
      expect(fixture.componentInstance.probePassed()).toBe(true);
      http.verify();
    } finally {
      vi.useRealTimers();
    }
  });

  it('shows the assigned project delivery failure and reason', () => {
    TestBed.configureTestingModule({
      imports: [ExecutionAssignmentCardComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    });
    const fixture = TestBed.createComponent(ExecutionAssignmentCardComponent);
    fixture.componentRef.setInput('projectName', 'Agent Studio');
    fixture.detectChanges();

    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/projects/settings').flush({
      'Agent Studio': { pickupMode: 'auto', executionLocation: 'agent-runner-01' },
    });
    const clients = http.match('/api/clients');
    clients.forEach(request => request.flush([]));
    http.expectOne('/api/v1/management/remote-hosts').flush([]);
    const links = http.match('/api/v1/management/links');
    links.forEach(request => request.flush([]));
    TestBed.inject(RemoteHostsService).hosts.set([{
      id: 'agent-runner-01', name: 'Runner 01', role: 'remote', address: null,
      clientId: 'agent-runner-01', status: 'online', os: 'Linux', lastHeartbeatAt: null,
      uptimeLabel: null, capabilities: ['git'], cliQuotas: [], stats: null,
      projectPreflights: [{
        projectId: 'PROJ-001', projectName: 'Agent Studio', registrationFingerprint: 'b'.repeat(64),
        repositoryUrl: 'https://example.test/studio.git', fetchUrl: 'https://example.test/studio.git',
        pushUrl: 'https://example.test/studio.git', targetBranch: 'develop', status: 'failed',
        detail: 'write probe failed: permission denied', checkedAt: '2026-07-22T10:00:00Z',
      }],
    }]);
    fixture.detectChanges();

    const status = fixture.nativeElement.querySelector('[data-testid="project-delivery-preflight"]');
    expect(status?.textContent).toContain('blocked');
    expect(status?.textContent).toContain('Target develop');
    expect(status?.textContent).toContain('permission denied');
    http.verify();
  });
});

function flushHostRegistryFailure(http: HttpTestingController): void {
  http.expectOne('/api/clients').flush(
    { error: 'registry unavailable in component unit test' },
    { status: 503, statusText: 'Service Unavailable' },
  );
}
