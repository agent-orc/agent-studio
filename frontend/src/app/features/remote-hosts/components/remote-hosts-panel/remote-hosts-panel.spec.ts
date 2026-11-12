import { beforeEach, describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { RemoteHostsPanelComponent } from './remote-hosts-panel';
import { RemoteHostsService } from '../../services/remote-hosts.service';
import { ReviewQueueService, type ReviewQueueSnapshot } from '../../services/review-queue.service';
import { AuthSessionState } from '../../../../services/auth.service';
import type { RemoteHost } from '../../models/remote-host.model';

function codingHost(overrides: Partial<RemoteHost> = {}): RemoteHost {
  return {
    id: 'agent-runner-01',
    name: 'agent-runner-01',
    role: 'remote',
    serviceRole: 'coding',
    address: null,
    clientId: 'agent-runner-01',
    capacityHostId: 'agent-runner-01',
    status: 'online',
    os: 'Linux',
    lastHeartbeatAt: '2026-11-09T09:30:00Z',
    uptimeLabel: null,
    capabilities: [],
    cliQuotas: [],
    stats: null,
    liveDataState: 'ready',
    ...overrides,
  };
}

function reviewSnapshot(overrides: Partial<ReviewQueueSnapshot> = {}): ReviewQueueSnapshot {
  return {
    queueDepth: 3,
    activeJobs: 2,
    isStagnant: false,
    stagnantSince: null,
    stagnantThresholdMinutes: 20,
    drainRatePerMinute: 1.4,
    medianReviewDurationMs: 190_000,
    throughputWindowMinutes: 60,
    observedAt: '2026-08-17T12:00:00Z',
    ...overrides,
  };
}

/**
 * Render-path test: the panel loads its registry on init and renders one table
 * row per host with a summary line whose counts reconcile to the visible rows
 * (R3 sum invariant).
 */
describe('RemoteHostsPanelComponent', () => {
  beforeEach(() => localStorage.removeItem('atp.environment.view.v1.local'));
  it('adds the API host after the networked profile arrives', async () => {
    await TestBed.configureTestingModule({
      imports: [RemoteHostsPanelComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
    const auth = TestBed.inject(AuthSessionState);
    const http = TestBed.inject(HttpTestingController);
    const hosts = TestBed.inject(RemoteHostsService).hosts;
    const fixture = TestBed.createComponent(RemoteHostsPanelComponent);
    fixture.detectChanges();
    http.expectNone('/api/host/filesystems');

    auth.status.set({ profile: 'networked', authenticated: true, bootstrapRequired: false });
    fixture.detectChanges();
    http.expectOne('/api/host/filesystems').flush({
      host: 'studio-api', observedAt: '2026-11-10T12:00:00Z', level: 'warning',
      reason: 'Instance data is low.', filesystems: [{ purpose: 'instance data', path: '/data',
        freeBytes: 12, totalBytes: 100 }],
    });
    for (const request of http.match('/api/clients')) request.flush([]);
    http.expectOne('/api/v1/management/remote-hosts').flush([]);
    fixture.detectChanges();
    expect(hosts().map(host => host.id)).toEqual(['api-host']);
    fixture.destroy();
  });

  it('shows provider request refusals by model and day', async () => {
    await TestBed.configureTestingModule({
      imports: [RemoteHostsPanelComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const service = TestBed.inject(RemoteHostsService);
    service.providerRefusals.set([{
      day: '2026-09-18',
      model: 'gpt-6-astra',
      count: 2,
      refusals: ['unsupported_parameter access_programs.cyber'],
    }]);

    const fixture = TestBed.createComponent(RemoteHostsPanelComponent);
    fixture.componentInstance.setView('history');
    fixture.detectChanges();
    const summary = (fixture.nativeElement as HTMLElement).querySelector(
      '[data-testid="provider-refusal-summary"]',
    );

    expect(summary?.textContent).toContain('gpt-6-astra');
    expect(summary?.textContent).toContain('2026-09-18');
    expect(summary?.textContent).toContain('unsupported_parameter access_programs.cyber');
    fixture.destroy();
  });

  it('shows claim gate stops and resumes with host, load and duration in History', async () => {
    await TestBed.configureTestingModule({
      imports: [RemoteHostsPanelComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
    const service = TestBed.inject(RemoteHostsService);
    const fixture = TestBed.createComponent(RemoteHostsPanelComponent);
    fixture.detectChanges();
    service.hosts.set([codingHost({ name: 'Coding runner' })]);
    fixture.componentInstance.setView('history');
    service.claimGateEvents.set([
      { sequence: 2, occurredAt: '2026-11-09T09:30:00Z', runnerId: 'agent-runner-01', hostId: 'agent-runner-01',
        event: 'claim-resumed', loadPerCore: 3.2, threshold: 4, durationSeconds: 1380, activeSlots: 2 },
      { sequence: 1, occurredAt: '2026-11-09T09:07:00Z', runnerId: 'unknown-runner', hostId: null,
        event: 'claim-stopped', loadPerCore: 5.51, threshold: 4, durationSeconds: 702, activeSlots: 4 },
    ]);
    fixture.detectChanges();
    const rows = [...(fixture.nativeElement as HTMLElement).querySelectorAll('[data-testid="claim-gate-event"]')];
    expect(rows.map(row => row.getAttribute('data-event'))).toEqual(['claim-resumed', 'claim-stopped']);
    expect(rows[0].textContent).toContain('Coding runner');
    expect(rows[0].textContent).toContain('Claim resumed');
    expect(rows[0].textContent).toContain('Not claiming for 23 min · load 3.2 per core, threshold 4.0');
    expect(rows[1].textContent).toContain('unknown-runner');
    expect(rows[1].textContent).toContain('Load 5.5 per core above 4.0 for 12 min');
    fixture.destroy();
  });

  it('summary buckets follow the status each machine row shows (R3)', async () => {
    await TestBed.configureTestingModule({
      imports: [RemoteHostsPanelComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
    const service = TestBed.inject(RemoteHostsService);
    const fixture = TestBed.createComponent(RemoteHostsPanelComponent);
    fixture.detectChanges();
    const base = { role: 'remote' as const, address: null, os: 'Linux', lastHeartbeatAt: null, uptimeLabel: null,
      capabilities: [], cliQuotas: [], stats: null, liveDataState: 'ready' as const };
    service.hosts.set([
      { ...base, id: 'offline-unenrolled', name: 'offline-unenrolled', clientId: 'offline-unenrolled', status: 'offline' },
      { ...base, id: 'local', name: 'local', clientId: 'local', role: 'local', status: 'online' },
      { ...base, id: 'draining', name: 'draining', clientId: 'draining', status: 'draining',
        runnerInstanceId: 'i', capacityHostId: 'draining' },
    ]);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const count = (key: string) => Number(el.querySelector(`[data-testid="remote-hosts-summary-${key}"] strong`)?.textContent);
    const rows = [...el.querySelectorAll('[data-testid="remote-host-primary-row"]')];
    const shown = (label: string) => rows.filter(row => row.textContent?.includes(label)).length;
    expect(count('machines')).toBe(rows.length);
    expect(count('offline')).toBe(shown('Offline'));
    expect(count('reporting')).toBe(shown('Online') + shown('Idle'));
    expect(count('draining')).toBe(shown('Draining'));
    expect(count('degraded')).toBe(0);
    expect(count('reporting') + count('degraded') + count('draining') + count('offline')).toBe(rows.length);
    expect(count('not-enrolled')).toBe(el.querySelectorAll('[data-testid="remote-host-not-enrolled"]').length);
    fixture.destroy();
  });

  it('counts a gate worker as a role only when it has a role row under its machine (R3)', async () => {
    await TestBed.configureTestingModule({
      imports: [RemoteHostsPanelComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
    const service = TestBed.inject(RemoteHostsService);
    const fixture = TestBed.createComponent(RemoteHostsPanelComponent);
    fixture.detectChanges();
    const base = { role: 'remote' as const, address: null, os: 'Linux', lastHeartbeatAt: null, uptimeLabel: null,
      capabilities: [], cliQuotas: [], stats: null, liveDataState: 'ready' as const };
    service.hosts.set([{ ...base, id: 'runner-a', name: 'runner-a', clientId: 'runner-a', status: 'online' }]);
    const gate = { slots: 2, busy: 1, processorCount: 4, lastHeartbeat: '2026-11-09T14:00:00Z', currentRun: null };
    fixture.componentInstance.environment.set({
      controlHost: 'control', installationId: null, mode: null, routes: null, gateHost: null, gateSlots: null,
      engine: { state: 'running', lastContact: null, engineId: null },
      gateWorkers: [{ ...gate, id: 'gate-on-runner', name: 'runner-a' }, { ...gate, id: 'gate-alone', name: 'gate-box' }],
    });
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const count = (key: string) => Number(el.querySelector(`[data-testid="remote-hosts-summary-${key}"] strong`)?.textContent);
    expect(count('machines')).toBe(el.querySelectorAll('[data-testid="remote-host-primary-row"]').length);
    expect(count('machines')).toBe(2);
    expect(count('roles')).toBe(el.querySelectorAll('.host-list__row--role').length);
    expect(count('roles')).toBe(2);
    fixture.destroy();
  });

  it('mounts a machine list and selected detail with setup and add actions', async () => {
    await TestBed.configureTestingModule({
      imports: [RemoteHostsPanelComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const service = TestBed.inject(RemoteHostsService);
    const fixture = TestBed.createComponent(RemoteHostsPanelComponent);
    fixture.detectChanges();
    service.hosts.set([codingHost()]);
    fixture.detectChanges();
    const el: HTMLElement = fixture.nativeElement;

    expect(el.querySelector('[data-testid="remote-hosts-panel"]')).toBeTruthy();
    expect(el.querySelector('h2')?.textContent).toContain('Environment');
    const rows = el.querySelectorAll('[data-testid="remote-host-primary-row"]');
    expect(rows.length).toBe(fixture.componentInstance.total());
    expect(el.querySelector('[data-testid="remote-hosts-table"]')).toBeTruthy();
    expect(el.querySelector('[data-testid="remote-hosts-summary"]')?.textContent)
      .toContain(String(rows.length));

    fixture.componentInstance.selectHost('agent-runner-01');
    fixture.detectChanges();
    const detail = el.querySelector('[data-testid="remote-host-card"]') as HTMLElement;
    (detail.querySelector('[data-testid="remote-host-detail-toggle-connection"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    (detail.querySelector('[data-testid="remote-host-action-setup"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(fixture.componentInstance.setupHost()?.id).toBe('agent-runner-01');
    expect(el.querySelector('[data-testid="runner-setup-dialog"]')).toBeTruthy();
    fixture.componentInstance.closeSetup();
    fixture.detectChanges();

    const addButton = el.querySelector('[data-testid="remote-hosts-add"]') as HTMLButtonElement;
    addButton.click();
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="add-host-wizard"]')).toBeTruthy();
    expect(el.querySelector('#add-host-title')?.textContent).toContain('Add an execution host');

    fixture.destroy();
  });

  /**
   * AGT-2826: the release column has to answer "is this host running what
   * Stable runs?" without the operator reading a host symlink. The Stable
   * version sits in the header and every role row carries its own release plus
   * the age it lags.
   */
  it('shows the Stable release and marks each role that lags it', async () => {
    await TestBed.configureTestingModule({
      imports: [RemoteHostsPanelComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const service = TestBed.inject(RemoteHostsService);
    service.stableRelease.set({ version: '0.3.0', commit: 'aaaaaaa1111', builtAt: '2026-09-11T08:00:00Z' });
    service.hosts.set([
      {
        id: 'agent-runner-01', name: 'agent-runner-01', role: 'remote', serviceRole: 'coding',
        address: null, clientId: 'agent-runner-01', capacityHostId: 'agent-runner-01',
        status: 'online', os: 'Linux', lastHeartbeatAt: '2026-09-15T11:59:00Z', uptimeLabel: null,
        capabilities: [], cliQuotas: [], stats: null, liveDataState: 'ready',
        releaseId: 'agt-host-20260823T060000Z-bbbbbbb',
        release: {
          releaseId: 'agt-host-20260823T060000Z-bbbbbbb', version: '0.2.7',
          commit: 'bbbbbbb2222', builtAt: '2026-08-23T06:00:00Z',
        },
        releaseDrift: {
          runnerId: 'agent-runner-01', name: 'agent-runner-01', hostId: 'agent-runner-01',
          role: 'coding', state: 'behind', behindByHours: 458, behindForHours: 458,
          alarmDue: true, heartbeatStale: false, lastSeenAt: '2026-09-15T11:59:00Z',
          reason: 'The host build is 19d 2h older than the Stable build.',
          release: {
            releaseId: 'agt-host-20260823T060000Z-bbbbbbb', version: '0.2.7',
            commit: 'bbbbbbb2222', builtAt: '2026-08-23T06:00:00Z',
          },
        },
      },
      {
        id: 'agent-runner-01-review', name: 'agent-runner-01-review', role: 'remote', serviceRole: 'review',
        address: null, clientId: 'agent-runner-01-review', capacityHostId: 'agent-runner-01',
        status: 'online', os: 'Linux', lastHeartbeatAt: '2026-09-15T12:00:00Z', uptimeLabel: null,
        capabilities: [], cliQuotas: [], stats: null, liveDataState: 'ready',
        releaseId: 'agt-host-20260911T080000Z-ccccccc',
        release: {
          releaseId: 'agt-host-20260911T080000Z-ccccccc', version: '0.3.0',
          commit: 'ccccccc3333', builtAt: '2026-09-11T08:00:00Z',
        },
        releaseDrift: {
          runnerId: 'agent-runner-01-review', name: 'agent-runner-01-review', hostId: 'agent-runner-01',
          role: 'review', state: 'current', behindByHours: null, behindForHours: null,
          alarmDue: false, heartbeatStale: false, lastSeenAt: '2026-09-15T12:00:00Z',
          reason: 'The host runs the Stable release.',
          release: {
            releaseId: 'agt-host-20260911T080000Z-ccccccc', version: '0.3.0',
            commit: 'ccccccc3333', builtAt: '2026-09-11T08:00:00Z',
          },
        },
      },
    ]);

    const fixture = TestBed.createComponent(RemoteHostsPanelComponent);
    fixture.detectChanges();
    const el: HTMLElement = fixture.nativeElement;

    expect(el.querySelector('[data-testid="remote-hosts-stable-release"]')?.textContent)
      .toContain('Stable 0.3.0');
    expect(el.querySelector('[data-testid="remote-hosts-release-behind"]')?.textContent)
      .toContain('1');

    fixture.componentInstance.selectHost('agent-runner-01');
    fixture.detectChanges();
    const roleRelease = el.querySelector('[data-testid="remote-host-release"]');
    expect(roleRelease?.textContent).toContain('0.2.7');
    expect(roleRelease?.textContent).toContain('19d behind');

    fixture.destroy();
  });

  it('renders the corrupt identity recovery diagnostic', async () => {
    await TestBed.configureTestingModule({
      imports: [RemoteHostsPanelComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const service = TestBed.inject(RemoteHostsService);
    service.identityDiagnostics.set([{
      id: 'agent-runner-01', displayName: 'agent-runner-01', emoji: null, colour: null,
      kind: 'service', registeredAt: '2026-08-05T14:35:00Z', lastSeenAt: null,
      tokenBudgetMonthly: null, notes: null,
      identityFileError: 'identity file corrupt: agent-runner-01.json',
      identityFileName: 'agent-runner-01.json',
      identityFileModifiedAt: '2026-08-05T14:35:00Z',
      identityRestoreHint: 'Restore a valid file or re-register with POST /api/clients/register.',
    }]);

    const fixture = TestBed.createComponent(RemoteHostsPanelComponent);
    fixture.detectChanges();
    const diagnostic = (fixture.nativeElement as HTMLElement).querySelector(
      '[data-testid="environment-attention"]',
    );

    expect(diagnostic).toBeTruthy();
    const summary = diagnostic?.querySelector('button') as HTMLButtonElement;
    summary.click();
    fixture.detectChanges();
    expect(diagnostic?.textContent).toContain('identity file corrupt: agent-runner-01.json');
    expect(diagnostic?.textContent).toContain('POST /api/clients/register');
    fixture.destroy();
  });

  it('shows the toolbar "Delete retired…" action only once a retired host exists, and opens its dry-run dialog', async () => {
    await TestBed.configureTestingModule({
      imports: [RemoteHostsPanelComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(RemoteHostsPanelComponent);
    fixture.detectChanges();
    const el: HTMLElement = fixture.nativeElement;

    expect(el.querySelector('[data-testid="remote-hosts-purge-retired"]')).toBeNull();

    const service = TestBed.inject(RemoteHostsService);
    service.hosts.set([codingHost({ status: 'retired' })]);
    fixture.detectChanges();

    const purgeButton = el.querySelector('[data-testid="remote-hosts-purge-retired"]') as HTMLButtonElement;
    expect(purgeButton).toBeTruthy();
    purgeButton.click();
    fixture.detectChanges();
    // <app-dialog> portals itself onto <body>, so the rendered dialog is no
    // longer a descendant of the panel's own root element.
    expect(document.querySelector('[data-testid="purge-retired-dialog"]')).toBeTruthy();

    fixture.componentInstance.closePurgeRetired();
    fixture.detectChanges();
    expect(document.querySelector('[data-testid="purge-retired-dialog"]')).toBeNull();
    fixture.destroy();
  });

  it('routes a role-row delete action through the name-bearing confirmation modal', async () => {
    await TestBed.configureTestingModule({
      imports: [RemoteHostsPanelComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const service = TestBed.inject(RemoteHostsService);
    const fixture = TestBed.createComponent(RemoteHostsPanelComponent);
    fixture.detectChanges();
    service.hosts.set([codingHost()]);
    fixture.detectChanges();

    fixture.componentInstance.onAction({ kind: 'delete', id: 'agent-runner-01' });
    fixture.detectChanges();

    const pending = fixture.componentInstance.pendingConfirmation();
    expect(pending?.kind).toBe('delete');
    expect(fixture.componentInstance.confirmationTitle()).toContain('agent-runner-01');
    expect(fixture.componentInstance.confirmationText()).toContain('cannot be undone');
    fixture.destroy();
  });

  describe('auto-review queue summary', () => {
    async function mountWithReviewSnapshot(snapshot: ReviewQueueSnapshot | null) {
      await TestBed.configureTestingModule({
        imports: [RemoteHostsPanelComponent],
        providers: [
          provideZonelessChangeDetection(),
          provideHttpClient(),
          provideHttpClientTesting(),
          provideRouter([]),
        ],
      }).compileComponents();
      const reviewQueue = TestBed.inject(ReviewQueueService);
      reviewQueue.snapshot.set(snapshot);
      const fixture = TestBed.createComponent(RemoteHostsPanelComponent);
      fixture.detectChanges();
      return fixture;
    }

    it('shows a non-empty queue as a quiet Hosts value', async () => {
      const fixture = await mountWithReviewSnapshot(reviewSnapshot());
      const el: HTMLElement = fixture.nativeElement;
      expect(el.querySelector('[data-testid="auto-review-queue-summary"]')?.textContent).toContain('3 waiting');
      fixture.destroy();
    });

    it('moves an idle queue to Installation', async () => {
      const fixture = await mountWithReviewSnapshot(reviewSnapshot({ queueDepth: 0, activeJobs: 0 }));
      fixture.componentInstance.setView('installation');
      fixture.detectChanges();
      const el: HTMLElement = fixture.nativeElement;
      expect(el.querySelector('[data-testid="auto-review-queue-summary"]')?.textContent).toContain('0 waiting');
      fixture.destroy();
    });

    it('puts a stagnant queue in the open attention causes', async () => {
      const fixture = await mountWithReviewSnapshot(reviewSnapshot({
        queueDepth: 12, activeJobs: 0, isStagnant: true,
        stagnantSince: '2026-08-17T11:00:00Z', stagnantThresholdMinutes: 20,
      }));
      const el: HTMLElement = fixture.nativeElement;
      const attention = el.querySelector('[data-testid="environment-attention"]') as HTMLElement;
      (attention.querySelector('button') as HTMLButtonElement).click();
      fixture.detectChanges();
      expect(attention.textContent).toContain('20 minutes');
      fixture.destroy();
    });

    it('omits the summary section when no snapshot has loaded yet', async () => {
      const fixture = await mountWithReviewSnapshot(null);
      const el: HTMLElement = fixture.nativeElement;

      expect(el.querySelector('[data-testid="auto-review-queue-summary"]')).toBeFalsy();
      fixture.destroy();
    });
  });
});
