import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { RemoteHostsService } from './remote-hosts.service';

function flushLinkHealth(http: HttpTestingController): void {
  for (const request of http.match('/api/v1/management/remote-hosts/link-health')) request.flush([]);
}

describe('RemoteHostsService', () => {
  let svc: RemoteHostsService;

  beforeEach(() => {
    vi.useFakeTimers();
    svc = new RemoteHostsService();
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it('seeds the registry on first load with the local + remote hosts', () => {
    svc.ensureLoaded();
    const hosts = svc.hosts();
    expect(hosts.length).toBeGreaterThanOrEqual(2);
    expect(hosts[0]).toMatchObject({
      id: 'local',
      name: 'Local machine',
      role: 'local',
      clientId: 'local-default',
    });
    expect(hosts.some((h) => h.role === 'local')).toBe(true);
    expect(hosts.some((h) => h.role === 'remote')).toBe(true);
    expect(hosts.find((h) => h.id === 'agent-runner-01')).toMatchObject({
      clientId: 'agent-runner-01',
      status: 'offline',
      lastHeartbeatAt: null,
      cliQuotas: [],
      stats: null,
    });
    expect(svc.loading()).toBe(false);
    expect(svc.error()).toBeNull();
  });

  it('ensureLoaded revalidates live state on every mount', () => {
    const spy = vi.spyOn(svc, 'reload');
    svc.ensureLoaded();
    svc.ensureLoaded();
    expect(spy).toHaveBeenCalledTimes(2);
  });

  it('refresh initializes an empty registry before hydrating live data', () => {
    svc.refresh();

    expect(svc.hosts().length).toBeGreaterThan(0);
  });

  it('refreshes an already loaded registry without replacing its visible entries', () => {
    svc.ensureLoaded();
    svc.addProvisionedHost('Runner Berlin 02', 'ssh://runner@berlin.example');

    svc.refresh();

    expect(svc.hosts().some((host) => host.id === 'runner-berlin-02')).toBe(true);
  });

  it('adds a wizard-completed host as an idle remote runner', () => {
    svc.ensureLoaded();
    svc.addProvisionedHost('Runner Berlin 02', 'ssh://runner@berlin.example');

    const host = svc.hosts().find((item) => item.id === 'runner-berlin-02');
    expect(host).toMatchObject({
      name: 'Runner Berlin 02',
      address: 'ssh://runner@berlin.example',
      clientId: 'runner-berlin-02',
      role: 'remote',
      status: 'idle',
    });
  });

});

describe('RemoteHostsService client registry hydration', () => {
  it('surfaces a corrupt identity and does not request telemetry for it', () => {
    TestBed.configureTestingModule({
      providers: [RemoteHostsService, provideHttpClient(), provideHttpClientTesting()],
    });
    const svc = TestBed.inject(RemoteHostsService);
    const http = TestBed.inject(HttpTestingController);

    svc.reload();
    http.expectOne('/api/clients').flush([{
      id: 'agent-runner-01',
      displayName: 'agent-runner-01',
      emoji: null,
      colour: null,
      kind: 'service',
      registeredAt: '2026-08-05T14:35:00Z',
      lastSeenAt: null,
      tokenBudgetMonthly: null,
      notes: null,
      identityFileError: 'identity file corrupt: agent-runner-01.json',
      identityFileName: 'agent-runner-01.json',
      identityFileModifiedAt: '2026-08-05T14:35:00Z',
      identityRestoreHint: 'Restore a valid file or re-register with POST /api/clients/register.',
    }]);
    http.expectOne('/api/v1/management/remote-hosts').flush([]);

    expect(svc.identityDiagnostics()).toHaveLength(1);
    expect(svc.hosts().find(host => host.clientId === 'agent-runner-01')).toMatchObject({
      status: 'offline',
      identityFileError: 'identity file corrupt: agent-runner-01.json',
      telemetryLoading: false,
    });
    http.expectNone('/api/clients/agent-runner-01/telemetry?window=14d');
    flushLinkHealth(http);
    http.verify();
  });

  it('preserves a loaded 14-day series and replaces an updated active finding', () => {
    TestBed.configureTestingModule({
      providers: [RemoteHostsService, provideHttpClient(), provideHttpClientTesting()],
    });
    const svc = TestBed.inject(RemoteHostsService);
    const http = TestBed.inject(HttpTestingController);
    const now = new Date();
    const older = new Date(now.getTime() - 60_000).toISOString();
    const latest = now.toISOString();
    const client = {
      id: 'agent-runner-01',
      displayName: 'agent-runner-01',
      kind: 'service',
      registeredAt: older,
      lastSeenAt: latest,
      runnerGitStatus: 'ready' as const,
    };
    const point = (timestamp: string, load1: number) => ({
      timestamp,
      cpuPercent: 40,
      load1,
      load5: load1,
      load15: load1,
      memoryUsedBytes: 4_000_000_000,
      memoryTotalBytes: 16_000_000_000,
      swapInBytesPerSecond: 0,
      swapOutBytesPerSecond: 0,
      cpuStealPercent: 0,
      ioWaitPercent: 0,
      cpuCores: 12,
      activeSlots: 1,
    });

    svc.reload();
    http.expectOne('/api/clients').flush([client]);
    http.expectOne('/api/clients/agent-runner-01/telemetry?window=14d').flush({
      clientId: client.id,
      window: '14d',
      points: [point(older, 2)],
      findings: [{
        kind: 'oversubscribed',
        label: 'Oversubscribed',
        since: older,
        until: older,
        occurrences: 1,
        isActive: true,
      }],
    });
    http.expectOne('/api/v1/management/remote-hosts').flush([]);

    svc.refresh();
    http.expectOne('/api/clients').flush([client]);
    http.expectOne('/api/clients/agent-runner-01/telemetry?window=1h').flush({
      clientId: client.id,
      window: '1h',
      points: [point(latest, 3)],
      findings: [{
        kind: 'oversubscribed',
        label: 'Oversubscribed',
        since: older,
        until: latest,
        occurrences: 1,
        isActive: true,
      }],
    });
    http.expectOne('/api/v1/management/remote-hosts').flush([]);

    expect(svc.hosts().find(host => host.id === client.id)?.telemetry).toMatchObject({
      window: '14d',
      points: [{ timestamp: older }, { timestamp: latest }],
      findings: [{ kind: 'oversubscribed', since: older, until: latest, isActive: true }],
    });
    expect(svc.hosts().find(host => host.id === client.id)?.telemetry?.findings).toHaveLength(1);
    flushLinkHealth(http);
    http.verify();
  });

  it('projects fresh and stale LastSeen and takes retirement only from the server', () => {
    TestBed.configureTestingModule({
      providers: [RemoteHostsService, provideHttpClient(), provideHttpClientTesting()],
    });
    const svc = TestBed.inject(RemoteHostsService);
    const http = TestBed.inject(HttpTestingController);
    const now = Date.now();

    svc.reload();
    http.expectOne('/api/clients').flush([{
      id: 'agent-runner-01',
      displayName: 'agent-runner-01',
      emoji: null,
      colour: null,
      kind: 'agent-instance',
      registeredAt: new Date(now - 10_000).toISOString(),
      lastSeenAt: new Date(now - 30_000).toISOString(),
      tokenBudgetMonthly: null,
      notes: null,
      runnerGitStatus: 'read-only',
      runnerGitDetail: 'push-dry-run failed (128): permission denied',
      runnerActiveSlots: 1,
      runnerAvailableSlots: 19,
    }]);
    http.expectOne('/api/clients/agent-runner-01/telemetry?window=14d').flush({
      clientId: 'agent-runner-01', window: '14d', points: [], findings: [],
    });
    http.expectOne('/api/v1/management/remote-hosts').flush([]);
    expect(svc.hosts().find(host => host.id === 'agent-runner-01')).toMatchObject({
      status: 'online',
      lastHeartbeatAt: new Date(now - 30_000).toISOString(),
      gitPushStatus: 'read-only',
      gitPushDetail: 'push-dry-run failed (128): permission denied',
      activeTaskCount: 1,
      availableSlots: 19,
      liveDataState: 'ready',
    });

    svc.reload();
    http.expectOne('/api/clients').flush([{
      id: 'agent-runner-01',
      displayName: 'agent-runner-01',
      emoji: null,
      colour: null,
      kind: 'agent-instance',
      registeredAt: new Date(now - 300_000).toISOString(),
      lastSeenAt: new Date(now - 180_000).toISOString(),
      tokenBudgetMonthly: null,
      notes: null,
    }]);
    http.expectOne('/api/clients/agent-runner-01/telemetry?window=14d').flush({
      clientId: 'agent-runner-01', window: '14d', points: [], findings: [],
    });
    http.expectOne('/api/v1/management/remote-hosts').flush([]);
    expect(svc.hosts().find(host => host.id === 'agent-runner-01')?.status).toBe('degraded');

    svc.reload();
    http.expectOne('/api/clients').flush([{
      id: 'agent-runner-01', displayName: 'agent-runner-01', emoji: null, colour: null,
      kind: 'agent-instance', registeredAt: new Date(now - 900_000).toISOString(),
      lastSeenAt: new Date(now - 600_000).toISOString(), tokenBudgetMonthly: null, notes: null,
    }]);
    http.expectOne('/api/clients/agent-runner-01/telemetry?window=14d').flush({
      clientId: 'agent-runner-01', window: '14d', points: [], findings: [],
    });
    http.expectOne('/api/v1/management/remote-hosts').flush([]);
    expect(svc.hosts().find(host => host.id === 'agent-runner-01')?.status).toBe('offline');

    svc.reload();
    http.expectOne('/api/clients').flush([{
      id: 'agent-runner-01', displayName: 'agent-runner-01', emoji: null, colour: null,
      kind: 'retired', registeredAt: new Date(now).toISOString(),
      lastSeenAt: new Date(now).toISOString(), tokenBudgetMonthly: null, notes: null,
    }]);
    expect(svc.hosts().find(host => host.id === 'agent-runner-01')?.status).toBe('retired');
    http.expectOne('/api/v1/management/remote-hosts').flush([]);
    flushLinkHealth(http);
    http.verify();
  });

  it('projects the versioned workflow-push capability without closing task inflow', () => {
    TestBed.configureTestingModule({
      providers: [RemoteHostsService, provideHttpClient(), provideHttpClientTesting()],
    });
    const svc = TestBed.inject(RemoteHostsService);
    const http = TestBed.inject(HttpTestingController);
    const now = new Date().toISOString();
    const capability = (key: string, advertisedStatus: string, detail: string | null) => ({
      key,
      category: 'source',
      advertisedStatus,
      healthState: 'healthy',
      reason: null,
      advertisedAt: now,
      freshUntil: new Date(Date.now() + 120_000).toISOString(),
      isFresh: true,
      firstFailureAt: null,
      lastFailureAt: null,
      cooldownUntil: null,
      canaryClaimId: null,
      consecutiveFailures: 0,
      version: 'available',
      identity: 'https://github.com/example/repo.git',
      detail,
      affectedClaims: [],
      recoveryHistory: [],
    });

    svc.reload();
    http.expectOne('/api/clients').flush([]);
    http.expectOne('/api/v1/management/remote-hosts').flush([{
      runnerId: 'agent-runner-01',
      name: 'agent-runner-01',
      hostId: 'runner-host',
      instanceId: 'runner-host:42',
      runnerVersion: '1.0.0',
      protocolVersion: 2,
      status: 'active',
      registeredAt: now,
      lastSeenAt: now,
      hostAdmission: {
        hostId: 'runner-host',
        admissionState: 'open',
        automaticDrainReason: null,
        automaticDrainAt: null,
        operatorDrainReason: null,
        operatorDrainAt: null,
      },
      capabilities: [
        capability('git:push', 'ready', 'contents ready'),
        capability('git:workflow-push', 'ready-no-workflow-scope', 'workflow scope missing'),
      ],
      telemetry: null,
    }]);

    expect(svc.hosts().find(host => host.id === 'agent-runner-01')).toMatchObject({
      gitPushStatus: 'ready-no-workflow-scope',
      gitPushDetail: 'workflow scope missing',
      releaseId: '1.0.0',
      runnerInstanceId: 'runner-host:42',
      runnerProtocolVersion: 2,
    });
    flushLinkHealth(http);
    http.verify();
  });

  it('persists drain through the lifecycle API before reloading', () => {
    TestBed.configureTestingModule({ providers: [RemoteHostsService, provideHttpClient(), provideHttpClientTesting()] });
    const svc = TestBed.inject(RemoteHostsService);
    const http = TestBed.inject(HttpTestingController);
    svc.reload();
    http.expectOne('/api/clients').flush([]);
    http.expectOne('/api/v1/management/remote-hosts').flush([]);

    svc.drain('agent-runner-01');
    expect(svc.hosts().find(host => host.id === 'agent-runner-01')?.busyAction).toBe('drain');
    http.expectOne('/api/clients/agent-runner-01/drain').flush({ id: 'agent-runner-01' });
    expect(svc.hosts().find(host => host.id === 'agent-runner-01')?.busyAction).toBeNull();
    http.expectOne('/api/clients').flush([{ id: 'agent-runner-01', displayName: 'agent-runner-01', kind: 'service', registeredAt: new Date().toISOString(), lastSeenAt: null, drainRequestedAt: new Date().toISOString() }]);
    http.expectOne('/api/clients/agent-runner-01/telemetry?window=14d').flush({ clientId: 'agent-runner-01', window: '14d', points: [], findings: [] });
    http.expectOne('/api/v1/management/remote-hosts').flush([]);
    expect(svc.hosts().find(host => host.id === 'agent-runner-01')?.status).toBe('draining');
    flushLinkHealth(http);
    http.verify();
  });

  it('projects live slot occupancy and role-local capacity from the Task Server snapshot', () => {
    TestBed.configureTestingModule({
      providers: [RemoteHostsService, provideHttpClient(), provideHttpClientTesting()],
    });
    const svc = TestBed.inject(RemoteHostsService);
    const http = TestBed.inject(HttpTestingController);
    const now = new Date().toISOString();

    svc.reload();
    http.expectOne('/api/clients').flush([]);
    http.expectOne('/api/v1/management/remote-hosts').flush([{
      runnerId: 'agent-runner-01',
      name: 'agent-runner-01',
      hostId: 'runner-host',
      instanceId: 'runner-host:42',
      runnerVersion: '1.0.0',
      protocolVersion: 3,
      status: 'active',
      registeredAt: now,
      lastSeenAt: now,
      hostAdmission: { hostId: 'runner-host', admissionState: 'open' },
      capabilities: [{
        key: 'executor:coding', category: 'executor', advertisedStatus: 'ready',
        healthState: 'healthy', advertisedAt: now, freshUntil: now, isFresh: true,
        consecutiveFailures: 0, affectedClaims: [], recoveryHistory: [],
      }],
      telemetry: {
        observedAt: now,
        cpuPercent: 40,
        memoryUsedBytes: 4_000_000_000,
        memoryTotalBytes: 16_000_000_000,
        cpuCores: 8,
        activeSlots: 6,
      },
      roleMaxParallelism: 8,
    }]);

    expect(svc.hosts().find(host => host.id === 'agent-runner-01')).toMatchObject({
      activeTaskCount: 6,
      roleMaxParallelism: 8,
      serviceRole: 'coding',
    });
    flushLinkHealth(http);
    http.verify();
  });

  it('hydrates and updates the Task Server runtime capacity by host id and version', () => {
    TestBed.configureTestingModule({
      providers: [RemoteHostsService, provideHttpClient(), provideHttpClientTesting()],
    });
    const svc = TestBed.inject(RemoteHostsService);
    const http = TestBed.inject(HttpTestingController);
    const now = new Date().toISOString();
    const capacity = {
      hostId: 'host-a',
      maxParallelism: 4,
      targetLoadPercent: 80,
      rampStrategy: 'balanced' as const,
      version: 1,
      updatedAt: now,
    };

    svc.reload();
    http.expectOne('/api/clients').flush([]);
    http.expectOne('/api/v1/management/remote-hosts').flush([{
      runnerId: 'agent-runner-01',
      name: 'agent-runner-01',
      hostId: 'host-a',
      instanceId: 'host-a:1',
      runnerVersion: '1.0.0',
      protocolVersion: 3,
      status: 'active',
      registeredAt: now,
      lastSeenAt: now,
      hostAdmission: {
        hostId: 'host-a',
        admissionState: 'open',
        automaticDrainReason: null,
        automaticDrainAt: null,
        operatorDrainReason: null,
        operatorDrainAt: null,
      },
      capabilities: [],
      telemetry: null,
      runtimeCapacity: capacity,
      effectiveMaxParallelism: 4,
      runtimeCapacityAppliedAt: now,
      runtimeCapacityAppliedVersion: 1,
      projectPolicy: {
        hostId: 'host-a',
        allowAllProjects: false,
        allowedProjectIds: ['PROJ-001'],
        version: 2,
        updatedAt: now,
      },
    }]);

    svc.setCapacity('agent-runner-01', 6, 85, 'aggressive');
    const request = http.expectOne('/api/v1/hosts/host-a/runtime-capacity');
    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({
      maxParallelism: 6,
      targetLoadPercent: 85,
      rampStrategy: 'aggressive',
      expectedVersion: 1,
    });
    request.flush({
      ...capacity,
      maxParallelism: 6,
      targetLoadPercent: 85,
      rampStrategy: 'aggressive',
      version: 2,
    });

    expect(svc.hosts().find(host => host.id === 'agent-runner-01')).toMatchObject({
      runtimeCapacity: {
        maxParallelism: 6,
        targetLoadPercent: 85,
        rampStrategy: 'aggressive',
        version: 2,
      },
      effectiveMaxParallelism: 4,
      busyAction: null,
    });

    svc.setProjectPolicy('agent-runner-01', false, ['PROJ-002'], 2);
    const projectPolicyRequest = http.expectOne('/api/v1/hosts/host-a/project-policy');
    expect(projectPolicyRequest.request.method).toBe('PUT');
    expect(projectPolicyRequest.request.body).toEqual({
      allowAllProjects: false,
      allowedProjectIds: ['PROJ-002'],
      expectedVersion: 2,
    });
    projectPolicyRequest.flush({
      hostId: 'host-a',
      allowAllProjects: false,
      allowedProjectIds: ['PROJ-002'],
      version: 3,
      updatedAt: now,
    });
    expect(svc.hosts().find(host => host.id === 'agent-runner-01')?.projectPolicy)
      .toMatchObject({ version: 3, allowedProjectIds: ['PROJ-002'] });
    flushLinkHealth(http);
    http.verify();
  });

  it('hydrates and updates monolith host capacity through the client identity', () => {
    TestBed.configureTestingModule({
      providers: [RemoteHostsService, provideHttpClient(), provideHttpClientTesting()],
    });
    const svc = TestBed.inject(RemoteHostsService);
    const http = TestBed.inject(HttpTestingController);
    const now = new Date().toISOString();
    const client = {
      id: 'agent-runner-01',
      displayName: 'agent-runner-01',
      kind: 'service',
      registeredAt: now,
      lastSeenAt: now,
      runnerActiveSlots: 2,
      runnerAvailableSlots: 2,
      runnerDesiredMaxParallelism: 4,
      runnerTargetLoadPercent: 80,
      runnerRampStrategy: 'balanced',
      runnerEffectiveMaxParallelism: 4,
      runnerEffectiveMaxParallelismAppliedAt: now,
    };

    svc.reload();
    http.expectOne('/api/clients').flush([client]);
    http.expectOne('/api/clients/agent-runner-01/telemetry?window=14d').flush({
      clientId: 'agent-runner-01', window: '14d', points: [], findings: [],
    });
    // No Task Server record: the client identity is the capacity owner, marked
    // by version 0 so the write goes to the monolith route.
    http.expectOne('/api/v1/management/remote-hosts').flush([]);
    expect(svc.hosts().find(host => host.id === 'agent-runner-01')).toMatchObject({
      runtimeCapacity: {
        maxParallelism: 4,
        targetLoadPercent: 80,
        rampStrategy: 'balanced',
        version: 0,
      },
      effectiveMaxParallelism: 4,
    });

    svc.setCapacity('agent-runner-01', 6, 85, 'conservative');
    const request = http.expectOne('/api/clients/agent-runner-01/runner-capacity');
    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({
      maxParallelism: 6,
      targetLoadPercent: 85,
      rampStrategy: 'conservative',
    });
    request.flush({
      ...client,
      runnerDesiredMaxParallelism: 6,
      runnerTargetLoadPercent: 85,
      runnerRampStrategy: 'conservative',
      runnerAvailableSlots: 4,
    });

    expect(svc.hosts().find(host => host.id === 'agent-runner-01')).toMatchObject({
      runtimeCapacity: {
        maxParallelism: 6,
        targetLoadPercent: 85,
        rampStrategy: 'conservative',
        version: 0,
      },
      availableSlots: 4,
      busyAction: null,
    });
    flushLinkHealth(http);
    http.verify();
  });

  it('never lets the client poll demote a Task Server capacity record', () => {
    TestBed.configureTestingModule({
      providers: [RemoteHostsService, provideHttpClient(), provideHttpClientTesting()],
    });
    const svc = TestBed.inject(RemoteHostsService);
    const http = TestBed.inject(HttpTestingController);
    const now = new Date().toISOString();
    const client = {
      id: 'agent-runner-01',
      displayName: 'agent-runner-01',
      kind: 'service',
      registeredAt: now,
      lastSeenAt: now,
      // The monolith identity carries a stale migration seed of its own.
      runnerDesiredMaxParallelism: 2,
      runnerTargetLoadPercent: 80,
      runnerRampStrategy: 'balanced',
      runnerEffectiveMaxParallelism: 5,
      runnerEffectiveMaxParallelismAppliedAt: now,
    };
    const snapshot = {
      runnerId: 'agent-runner-01',
      name: 'agent-runner-01',
      hostId: 'host-a',
      instanceId: 'host-a:1',
      runnerVersion: '1.0.0',
      protocolVersion: 3,
      status: 'active',
      registeredAt: now,
      lastSeenAt: now,
      hostAdmission: {
        hostId: 'host-a',
        admissionState: 'open',
        automaticDrainReason: null,
        automaticDrainAt: null,
        operatorDrainReason: null,
        operatorDrainAt: null,
      },
      capabilities: [],
      telemetry: null,
      runtimeCapacity: {
        hostId: 'host-a',
        maxParallelism: 6,
        targetLoadPercent: 85,
        rampStrategy: 'conservative' as const,
        version: 3,
        updatedAt: now,
      },
      effectiveMaxParallelism: 6,
      runtimeCapacityAppliedAt: now,
      runtimeCapacityAppliedVersion: 3,
    };

    svc.reload();
    http.expectOne('/api/clients').flush([client]);
    http.expectOne('/api/clients/agent-runner-01/telemetry?window=14d').flush({
      clientId: 'agent-runner-01', window: '14d', points: [], findings: [],
    });
    http.expectOne('/api/v1/management/remote-hosts').flush([snapshot]);

    // Second poll: the client identity answers again, and must not overwrite the
    // versioned record with its own version-0 projection.
    svc.reload();
    http.expectOne('/api/clients').flush([client]);
    http.expectOne('/api/clients/agent-runner-01/telemetry?window=14d').flush({
      clientId: 'agent-runner-01', window: '14d', points: [], findings: [],
    });
    http.expectOne('/api/v1/management/remote-hosts').flush([{
      ...snapshot,
      effectiveMaxParallelism: null,
      runtimeCapacityAppliedAt: null,
      runtimeCapacityAppliedVersion: null,
    }]);

    expect(svc.hosts().find(host => host.id === 'agent-runner-01')).toMatchObject({
      runtimeCapacity: { maxParallelism: 6, rampStrategy: 'conservative', version: 3 },
      effectiveMaxParallelism: null,
      runtimeCapacityAppliedAt: null,
      runtimeCapacityAppliedVersion: null,
    });

    // And the write still goes to the versioned Task Server route.
    svc.setCapacity('agent-runner-01', 7, 85, 'conservative');
    const request = http.expectOne('/api/v1/hosts/host-a/runtime-capacity');
    expect(request.request.body).toMatchObject({ expectedVersion: 3 });
    request.flush({ ...snapshot.runtimeCapacity, maxParallelism: 7, version: 4 });
    flushLinkHealth(http);
    http.verify();
  });

  it('publishes a first ceiling for a host that has none through the client route', () => {
    TestBed.configureTestingModule({
      providers: [RemoteHostsService, provideHttpClient(), provideHttpClientTesting()],
    });
    const svc = TestBed.inject(RemoteHostsService);
    const http = TestBed.inject(HttpTestingController);
    const now = new Date().toISOString();
    const client = {
      id: 'agent-runner-01',
      displayName: 'agent-runner-01',
      kind: 'service',
      registeredAt: now,
      lastSeenAt: now,
      runnerActiveSlots: 1,
    };

    svc.reload();
    http.expectOne('/api/clients').flush([client]);
    http.expectOne('/api/clients/agent-runner-01/telemetry?window=14d').flush({
      clientId: 'agent-runner-01', window: '14d', points: [], findings: [],
    });
    http.expectOne('/api/v1/management/remote-hosts').flush([]);
    // Nobody ever declared a capacity: the row shows none, and that must stay a
    // starting point rather than blocking the write.
    expect(svc.hosts().find(host => host.id === 'agent-runner-01')?.runtimeCapacity ?? null)
      .toBeNull();

    svc.setCapacity('agent-runner-01', 4, 80, 'balanced');
    const request = http.expectOne('/api/clients/agent-runner-01/runner-capacity');
    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({
      maxParallelism: 4,
      targetLoadPercent: 80,
      rampStrategy: 'balanced',
    });
    request.flush({
      ...client,
      runnerDesiredMaxParallelism: 4,
      runnerTargetLoadPercent: 80,
      runnerRampStrategy: 'balanced',
      runnerAvailableSlots: 3,
    });

    expect(svc.hosts().find(host => host.id === 'agent-runner-01')).toMatchObject({
      runtimeCapacity: { maxParallelism: 4, targetLoadPercent: 80, version: 0 },
      availableSlots: 3,
      busyAction: null,
    });
    flushLinkHealth(http);
    http.verify();
  });

  it('invalidates cached project proofs before reloading a re-probed host', () => {
    TestBed.configureTestingModule({ providers: [RemoteHostsService, provideHttpClient(), provideHttpClientTesting()] });
    const svc = TestBed.inject(RemoteHostsService);
    const http = TestBed.inject(HttpTestingController);
    svc.reload();
    http.expectOne('/api/clients').flush([]);
    http.expectOne('/api/v1/management/remote-hosts').flush([]);

    svc.reprobe('agent-runner-01');
    expect(svc.hosts().find(host => host.id === 'agent-runner-01')?.busyAction).toBe('reprobe');
    http.expectOne('/api/clients/agent-runner-01/runner-project-preflights/invalidate').flush({ id: 'agent-runner-01' });
    http.expectOne('/api/clients').flush([]);
    http.expectOne('/api/v1/management/remote-hosts').flush([]);
    flushLinkHealth(http);
    http.verify();
  });
});
