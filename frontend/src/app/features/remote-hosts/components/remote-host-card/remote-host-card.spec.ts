import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { By } from '@angular/platform-browser';
import { AppTooltipDirective } from '../../../../components/tooltip/app-tooltip.directive';
import { HostTelemetryHistoryComponent } from '../host-telemetry-history/host-telemetry-history';
import { RemoteHostCardComponent } from './remote-host-card';
import type { RemoteHost } from '../../models/remote-host.model';
import { CodexSignInDialogService } from '../../services/codex-sign-in-dialog.service';

const HOST: RemoteHost = {
  id: 'hetzner',
  name: 'agent-runner',
  role: 'remote',
  address: 'ssh://agent@runner.hetzner',
  clientId: 'agent-runner',
  status: 'online',
  os: 'Ubuntu 24.04 LTS',
  lastHeartbeatAt: '2026-07-10T11:59:55Z',
  uptimeLabel: '2d 9h',
  capabilities: ['linux', 'playwright'],
  cliQuotas: [{ cliType: 'claude', plan: 'Max', windowLabel: '5h', usedPct: 63, resetLabel: 'in 1h' }],
  stats: {
    ramTotalMb: 62 * 1024,
    ramFreeMb: 38 * 1024,
    cpuCores: 8,
    cpuModel: 'Xeon',
    cpuLoadPct: 54,
    diskTotalGb: 240,
    diskFreeGb: 96,
  },
  activeTaskCount: 1,
  availableSlots: 19,
  releaseId: 'release-20260811.1',
  telemetry: {
    clientId: 'agent-runner',
    window: '1h',
    findings: [],
    points: [{
      timestamp: '2026-07-10T11:59:55Z',
      cpuPercent: 54,
      load1: 1.2,
      load5: 1,
      load15: 1,
      memoryUsedBytes: 24_000_000_000,
      memoryTotalBytes: 64_000_000_000,
      swapInBytesPerSecond: 0,
      swapOutBytesPerSecond: 0,
      cpuStealPercent: 0,
      ioWaitPercent: 0,
      cpuCores: 8,
      activeSlots: 1,
    }],
  },
};

function mount(host: RemoteHost, expanded = true, expandSections = true) {
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({
    imports: [RemoteHostCardComponent],
    providers: [provideZonelessChangeDetection()],
  });
  const fixture = TestBed.createComponent(RemoteHostCardComponent);
  fixture.componentRef.setInput('host', host);
  fixture.componentRef.setInput('roles', [host]);
  fixture.componentRef.setInput('roleActiveSlots', { [host.id]: 0 });
  fixture.componentRef.setInput('now', Date.parse('2026-07-10T12:00:00Z'));
  fixture.componentRef.setInput('expanded', expanded);
  fixture.detectChanges();
  if (expanded && expandSections) {
    const toggles = fixture.nativeElement.querySelectorAll(
      '[data-testid^="remote-host-detail-toggle-"]',
    ) as NodeListOf<HTMLButtonElement>;
    toggles.forEach(toggle => toggle.click());
    fixture.detectChanges();
  }
  return fixture;
}

describe('RemoteHostCardComponent', () => {
  it('keeps the primary table row compact until its detail is disclosed', () => {
    const el: HTMLElement = mount(HOST, false).nativeElement;
    expect(el.querySelector('[data-testid="remote-host-detail-row"]')).toBeNull();
    expect(el.querySelector('[data-testid="remote-host-slots-summary"]')?.textContent).toContain('0 / 20');
    expect(el.querySelector('[data-testid="remote-host-load"]')?.textContent).toContain('54%');
    expect(el.querySelector('[data-testid="remote-host-release"]')?.textContent)
      .toContain('release-20260811.1');
  });

  it('opens to compact one-line section summaries before revealing internals', () => {
    const fixture = mount(HOST, true, false);
    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelectorAll('[data-testid^="remote-host-detail-toggle-"]').length).toBe(7);
    expect(el.querySelector('[data-testid="remote-host-detail-toggle-capabilities"]')?.textContent)
      .toContain('2 capabilities ok');
    expect(el.querySelector('.meter')).toBeNull();

    (el.querySelector('[data-testid="remote-host-detail-toggle-load"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(el.querySelectorAll('.meter').length).toBe(3);
  });

  it('renders name, status badge, role, and the three vitals meters', () => {
    const el: HTMLElement = mount(HOST).nativeElement;
    expect(el.querySelector('[data-testid="remote-host-name"]')?.textContent).toContain('agent-runner');
    expect(el.querySelector('[data-testid="remote-host-status"]')?.textContent).toContain('Online');
    expect(el.querySelector('.host__role')?.textContent).toContain('Remote');
    expect(el.querySelectorAll('.meter').length).toBe(3);
    // RAM 24/62 GB used => 39%
    expect(el.querySelector('[data-meter="ram"] .meter__pct')?.textContent).toContain('39%');
    expect(el.querySelector('[data-testid="remote-host-run-pool"]')?.textContent).toContain('1 active');
    expect(el.querySelector('[data-testid="remote-host-gate-pool"]')).toBeNull();
  });

  it('shows a neutral live-loading state instead of cached stopped data', () => {
    const el: HTMLElement = mount({
      ...HOST,
      status: 'offline',
      lastHeartbeatAt: '2026-07-10T09:00:00Z',
      liveDataState: 'loading',
      stats: null,
    }).nativeElement;
    expect(el.querySelector('[data-testid="remote-host-status"]')?.textContent).toContain('Loading live status');
    expect(el.querySelector('[data-testid="remote-host-run-pool"]')?.textContent).toContain('Loading daemon telemetry');
    expect(el.textContent).not.toContain('Daemonstopped');
    expect(el.querySelector('[data-testid="remote-host-stale"]')).toBeNull();
  });

  it('reflects an acute status as a data-tone on the host element', () => {
    const el: HTMLElement = mount({ ...HOST, status: 'offline' }).nativeElement;
    // The host binding lands on the component host element itself.
    expect(el.getAttribute('data-tone')).toBe('error');
    expect(el.querySelector('[data-testid="remote-host-status"]')?.textContent).toContain('Offline');
  });

  it('shows the independent read-only badge when the startup push probe fails', () => {
    const fixture = mount({
      ...HOST,
      gitPushStatus: 'read-only',
      gitPushDetail: 'push-dry-run failed (128): permission denied',
    });
    const el: HTMLElement = fixture.nativeElement;
    const badge = el.querySelector('[data-testid="remote-host-git-status"]');
    expect(badge?.textContent).toContain('Fallback repo: blocked');
    const tooltip = fixture.debugElement
      .query(By.css('[data-testid="remote-host-git-status"]'))
      .injector.get(AppTooltipDirective);
    expect(tooltip.appTooltip()).toContain('permission denied');
    expect(badge?.getAttribute('data-tone')).toBe('error');
    expect(el.querySelector('[data-testid="remote-host-activity"]')?.textContent)
      .toContain('Task inflowopen');
  });

  it('shows per-provider auth state, probe detail, expiry warning, and latest transition', () => {
    const fixture = mount({
      ...HOST,
      capabilityHealth: [{
        key: 'cli-execution:claude', category: 'cli-execution', advertisedStatus: 'ready',
        healthState: 'healthy', advertisedAt: '2026-07-10T11:59:30Z',
        freshUntil: '2026-07-10T12:02:30Z', isFresh: true, consecutiveFailures: 0,
        affectedClaims: [], recoveryHistory: [],
      }, {
        key: 'provider-auth:claude', category: 'provider-auth', advertisedStatus: 'unavailable',
        healthState: 'healthy', advertisedAt: '2026-07-10T11:59:30Z',
        freshUntil: '2026-07-10T12:02:30Z', isFresh: true, consecutiveFailures: 0,
        detail: 'Not logged in', expiresAt: '2026-07-20T12:00:00Z', affectedClaims: [],
        recoveryHistory: [{
          occurredAt: '2026-07-10T11:59:30Z', fromState: 'ready', toState: 'unavailable',
          reason: 'Provider authentication probe changed from ready to unavailable.',
        }],
      }],
    });
    const badge = fixture.nativeElement.querySelector(
      '[data-testid="remote-host-provider-auth-claude"]',
    ) as HTMLElement;

    expect(badge.textContent).toContain('Claude');
    expect(badge.textContent).toContain('unavailable');
    expect(badge.getAttribute('data-state')).toBe('unavailable');
    expect(fixture.debugElement
      .query(By.css('[data-testid="remote-host-provider-auth-claude"]'))
      .injector.get(AppTooltipDirective).appTooltip()).toContain('Not logged in');
    expect(fixture.nativeElement.querySelector('[data-testid="remote-host-provider-auth-expiry-claude"]')?.textContent)
      .toContain('Expires in 10 days');
    expect(fixture.nativeElement.querySelector('[data-testid="remote-host-provider-auth-history-claude"]')?.textContent)
      .toContain('ready → unavailable');
  });

  it('offers Codex sign-in for an expiring host badge and keeps the SSH target host-owned', () => {
    const fixture = mount({
      ...HOST,
      capabilityHealth: [{
        key: 'provider-auth:codex', category: 'provider-auth', advertisedStatus: 'ready',
        healthState: 'healthy', advertisedAt: '2026-07-10T11:59:30Z',
        freshUntil: '2026-07-10T12:02:30Z', isFresh: true, consecutiveFailures: 0,
        detail: 'Credentials expire soon', signal: 'credentials-expiring',
        affectedClaims: [], recoveryHistory: [],
      }],
    });
    const signIn = fixture.nativeElement.querySelector(
      '[data-testid="remote-host-codex-sign-in"]',
    ) as HTMLButtonElement;

    expect(signIn).toBeTruthy();
    signIn.click();
    expect(TestBed.inject(CodexSignInDialogService).request()).toMatchObject({
      hostId: 'hetzner',
      sshTarget: 'agent@runner.hetzner',
    });
  });

  it('shows contents ready, workflow missing, and the documentation fix without blocking inflow', () => {
    const el: HTMLElement = mount({
      ...HOST,
      gitPushStatus: 'ready-no-workflow-scope',
      gitPushDetail: 'GitHub rejected .github/workflows/release.yml without workflow scope',
    }).nativeElement;

    expect(el.querySelector('[data-testid="remote-host-git-status"]')?.textContent)
      .toContain('Fallback repo: ok');
    const workflow = el.querySelector('[data-testid="remote-host-workflow-status"]');
    expect(workflow?.textContent).toContain('Fallback workflow: permission missing');
    expect(workflow?.getAttribute('data-tone')).toBe('warn');
    const fix = el.querySelector('[data-testid="remote-host-token-scope-fix"]');
    expect(fix?.textContent).toContain('Grant the token');
    expect(fix?.querySelector('a')?.getAttribute('href'))
      .toBe('https://github.com/agent-orc/agent-studio/blob/main/docs/operations/setup/linux-runner-host.md#token-requirements');
    expect(el.querySelector('[data-testid="remote-host-activity"]')?.textContent)
      .toContain('Task inflowopen');
  });

  it('shows the project and reason when a delivery preflight blocks claims', () => {
    const el: HTMLElement = mount({
      ...HOST,
      projectPreflights: [{
        projectId: 'PROJ-042', projectName: 'Payments', registrationFingerprint: 'a'.repeat(64),
        repositoryUrl: 'https://example.test/payments.git', fetchUrl: 'https://example.test/payments.git',
        pushUrl: 'https://example.test/payments.git', targetBranch: 'release', status: 'failed',
        detail: 'write probe failed: permission denied', checkedAt: '2026-07-10T11:59:00Z',
      }],
    }).nativeElement;
    const failure = el.querySelector('[data-testid="remote-host-project-preflight-failures"]');
    expect(failure?.textContent).toContain('Payments');
    expect(failure?.textContent).toContain('release');
    expect(failure?.textContent).toContain('permission denied');
  });

  it('emits an action event with the host id when a control is clicked', () => {
    const fixture = mount(HOST);
    let received: { kind: string; id: string } | null = null;
    fixture.componentInstance.action.subscribe((e) => (received = e));
    const btn = fixture.nativeElement.querySelector('[data-testid="remote-host-action-drain"]') as HTMLButtonElement;
    btn.click();
    expect(received).toEqual({ kind: 'drain', id: 'hetzner' });
  });

  it('offers setup only for active remote hosts and emits the selected host', () => {
    const remote = mount(HOST);
    let selected: RemoteHost | null = null;
    remote.componentInstance.setup.subscribe(host => { selected = host; });

    const setup = remote.nativeElement.querySelector('[data-testid="remote-host-action-setup"]') as HTMLButtonElement;
    expect(setup).toBeTruthy();
    setup.click();
    expect(selected).toEqual(HOST);

    const local = mount({ ...HOST, id: 'local', role: 'local', address: null });
    expect(local.nativeElement.querySelector('[data-testid="remote-host-action-setup"]')).toBeNull();
  });

  it('renders "no stats" when a host reports none (e.g. retired)', () => {
    const el: HTMLElement = mount({ ...HOST, status: 'retired', stats: null }).nativeElement;
    expect(el.querySelector('[data-testid="remote-host-no-stats"]')).toBeTruthy();
    expect(el.querySelectorAll('.meter').length).toBe(0);
  });

  it('hides stale metrics instead of presenting the last CPU value as live', () => {
    const el: HTMLElement = mount({ ...HOST, lastHeartbeatAt: '2026-07-08T12:00:00Z' }).nativeElement;
    expect(el.querySelector('[data-testid="remote-host-stale"]')?.textContent).toContain('last seen 2d ago');
    expect(el.querySelectorAll('.meter').length).toBe(0);
    expect(el.textContent).not.toContain('54%');
  });

  it('dims stale active-slot telemetry instead of presenting it as live', () => {
    const staleTelemetry = {
      ...HOST.telemetry!,
      points: HOST.telemetry!.points.map(point => ({ ...point, timestamp: '2026-07-10T11:50:00Z', activeSlots: 3 })),
    };
    const el: HTMLElement = mount({ ...HOST, telemetry: staleTelemetry }).nativeElement;
    const pool = el.querySelector('[data-testid="remote-host-run-pool"]');
    expect(pool?.textContent).toContain('3 active · stale');
    expect(pool?.classList).toContain('workload--stale');
    expect(el.querySelector('[data-testid="remote-host-slots-context"]')?.classList)
      .toContain('telemetry__context--stale');
  });

  it('shows a warning icon when fresh telemetry and board leases diverge', () => {
    const fixture = mount(HOST);
    fixture.componentRef.setInput('boardActiveSlots', 0);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="remote-host-running-divergence"]'))
      .toBeTruthy();
  });

  it('renders telemetry charts, slot context, findings, and switches windows', () => {
    const telemetry = {
      clientId: 'agent-runner', window: '14d',
      points: [
        { timestamp: '2026-07-10T11:00:00Z', cpuPercent: 42, load1: 5.8, load5: 5, load15: 4,
          memoryUsedBytes: 32e9, memoryTotalBytes: 64e9, swapInBytesPerSecond: 0, swapOutBytesPerSecond: 0,
          cpuStealPercent: 6, ioWaitPercent: 2, cpuCores: 12, activeSlots: 5 },
        { timestamp: '2026-07-10T11:59:30Z', cpuPercent: 54, load1: 6.4, load5: 6, load15: 5,
          memoryUsedBytes: 34e9, memoryTotalBytes: 64e9, swapInBytesPerSecond: 0, swapOutBytesPerSecond: 0,
          cpuStealPercent: 7, ioWaitPercent: 3, cpuCores: 12, activeSlots: 6 },
      ],
      findings: [{
        kind: 'vm-throttled' as const,
        label: 'VM throttled',
        since: '2026-07-10T11:58:00Z',
        until: '2026-07-10T11:59:30Z',
        occurrences: 1,
        isActive: true,
      }],
    };
    const fixture = mount({ ...HOST, telemetry });
    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelectorAll('[data-chart]').length).toBe(4);
    expect(el.querySelector('[data-testid="remote-host-slots-context"]')?.textContent).toContain('6 RUN active · host load 6.4 of 12 cores');
    expect(el.querySelector('[data-testid="remote-host-findings"]')?.textContent).toContain('VM throttled');
    (el.querySelector('[data-testid="remote-host-window-1h"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    const telemetryHistory = fixture.debugElement
      .query(By.directive(HostTelemetryHistoryComponent))
      .componentInstance as HostTelemetryHistoryComponent;
    expect(telemetryHistory.window()).toBe('1h');
  });

  it('aggregates ended phases and bounds the finding row with a more counter', () => {
    const point = {
      timestamp: '2026-07-10T11:59:30Z', cpuPercent: 54, load1: 20, load5: 18, load15: 15,
      memoryUsedBytes: 34e9, memoryTotalBytes: 64e9, swapInBytesPerSecond: 0, swapOutBytesPerSecond: 0,
      cpuStealPercent: 7, ioWaitPercent: 12, cpuCores: 12, activeSlots: 6,
    };
    const findings = [
      { kind: 'oversubscribed' as const, label: 'Oversubscribed', since: '2026-07-10T11:58:00Z', until: point.timestamp, occurrences: 1, isActive: true },
      { kind: 'oversubscribed' as const, label: 'Oversubscribed', since: '2026-07-10T11:58:00Z', until: '2026-07-10T11:59:00Z', occurrences: 1, isActive: true },
      { kind: 'vm-throttled' as const, label: 'VM throttled', since: '2026-07-10T11:57:00Z', until: point.timestamp, occurrences: 1, isActive: true },
      { kind: 'memory-pressure' as const, label: 'Memory pressure', since: '2026-07-10T08:00:00Z', until: '2026-07-10T09:00:00Z', occurrences: 3, isActive: false },
      { kind: 'oversubscribed' as const, label: 'Oversubscribed', since: '2026-07-10T07:00:00Z', until: '2026-07-10T08:00:00Z', occurrences: 2, isActive: false },
      { kind: 'vm-throttled' as const, label: 'VM throttled', since: '2026-07-10T06:00:00Z', until: '2026-07-10T07:00:00Z', occurrences: 4, isActive: false },
    ];

    const el: HTMLElement = mount({
      ...HOST,
      telemetry: { clientId: 'agent-runner', window: '14d', points: [point], findings },
    }).nativeElement;

    expect(el.querySelectorAll('[data-testid="remote-host-finding"]').length).toBe(3);
    expect(el.querySelectorAll('[data-finding-kind="oversubscribed"][data-finding-active="true"]').length).toBe(1);
    expect(el.querySelector('[data-testid="remote-host-findings"]')?.textContent).toContain('3× in window');
    expect(el.querySelector('[data-testid="remote-host-findings-more"]')?.textContent).toContain('+2 more');
  });

  it('shows the exact synchronized telemetry point selected on the shared plot', () => {
    const points = [
      { timestamp: '2026-07-10T11:00:00Z', cpuPercent: 42, load1: 5.8, load5: 5, load15: 4,
        memoryUsedBytes: 32e9, memoryTotalBytes: 64e9, swapInBytesPerSecond: 0, swapOutBytesPerSecond: 0,
        cpuStealPercent: 6, ioWaitPercent: 2, cpuCores: 12, activeSlots: 5 },
      { timestamp: '2026-07-10T11:30:00Z', cpuPercent: 54.25, load1: 6.4, load5: 6, load15: 5,
        memoryUsedBytes: 34.5e9, memoryTotalBytes: 64e9, swapInBytesPerSecond: 0, swapOutBytesPerSecond: 0,
        cpuStealPercent: 7, ioWaitPercent: 3, cpuCores: 12, activeSlots: 6 },
    ];
    const fixture = mount({ ...HOST, telemetry: { clientId: 'agent-runner', window: '14d', points, findings: [] } });
    const plots = fixture.nativeElement.querySelector('[data-testid="remote-host-telemetry-plots"]') as HTMLElement;
    plots.getBoundingClientRect = () => ({ left: 100, width: 200 } as DOMRect);
    const telemetryHistory = fixture.debugElement
      .query(By.directive(HostTelemetryHistoryComponent))
      .componentInstance as HostTelemetryHistoryComponent;

    telemetryHistory.showPoint({ currentTarget: plots, clientX: 290 } as unknown as PointerEvent);
    fixture.detectChanges();

    const tooltip = fixture.nativeElement.querySelector('[data-testid="remote-host-telemetry-tooltip"]') as HTMLElement;
    expect(tooltip.dataset['pointTimestamp']).toBe(points[1].timestamp);
    expect(tooltip.querySelector('[data-metric="cpu"]')?.textContent).toContain('54.25%');
    expect(tooltip.querySelector('[data-metric="memory"]')?.textContent).toContain('34.5 GB');
    expect(tooltip.querySelector('[data-metric="load"]')?.textContent).toContain('6.4 load');
    expect(tooltip.querySelector('[data-metric="slots"]')?.textContent).toContain('6 slots');
    expect(fixture.nativeElement.querySelectorAll('.telemetry__point').length).toBe(4);

    telemetryHistory.hidePoint({ pointerType: 'touch' } as PointerEvent);
    expect(telemetryHistory.hoveredIndex()).toBe(1);
    telemetryHistory.hidePoint({ pointerType: 'mouse' } as PointerEvent);
    expect(telemetryHistory.hoveredIndex()).toBeNull();
  });
});
