import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { cliTypeIcon, cliTypeLabel } from '../../../../services/format.util';
import type { CliType } from '../../../../models/task.model';
import { AppTooltipDirective } from '../../../../components/tooltip/app-tooltip.directive';
import { CapabilityHealthComponent } from '../capability-health/capability-health';
import { GitTokenCapabilityComponent } from '../git-token-capability/git-token-capability';
import { HostWorkloadSummaryComponent } from '../host-workload-summary/host-workload-summary';
import { HostTelemetryHistoryComponent } from '../host-telemetry-history/host-telemetry-history';
import { RemoteHostDetailSummaryComponent } from '../remote-host-detail-summary/remote-host-detail-summary';
import {
  RemoteHostRoleRowComponent,
  roleSlotTotal,
} from '../remote-host-role-row/remote-host-role-row';
import {
  RuntimeCapacityEditorComponent,
  type HostProjectPolicyChange,
  type RuntimeCapacityChange,
} from '../runtime-capacity-editor/runtime-capacity-editor';
import {
  clampPct,
  diskUsedPct,
  formatDisk,
  formatMemory,
  hostRoleLabel,
  hostIsStale,
  hostStatusLabel,
  hostStatusTone,
  meterTone,
  ramUsedPct,
  relativeHeartbeat,
  taskServerRouteStatus,
  type HostActionKind,
  type HostProjectSlots,
  type MeterTone,
  type RemoteHost,
} from '../../models/remote-host.model';
import { freshHostTelemetry, latestHostTelemetry } from '../../models/running-truth';
import { providerAuthBadgesForHost, signInTarget, type ProviderAuthBadge } from '../../models/provider-auth.model';
import { CodexSignInDialogService } from '../../services/codex-sign-in-dialog.service';

/** One meter row (RAM / CPU / Disk) resolved for the template. */
interface Meter {
  key: string;
  label: string;
  detail: string;
  pct: number;
  tone: MeterTone;
}

/**
 * One execution location in the Remote-Hosts table (AGT-1921 / AGT-2629): one
 * compact primary row followed by an optional detail row. The parent owns and
 * persists disclosure state so sorting never loses an operator's open rows.
 *
 * Status is encoded with a dot + a badge, and acute states (degraded / offline)
 * additionally wash the affected row with a warn / error tint, never a left
 * accent bar (style-guide R1). History (retired / draining) renders calm (R4).
 */
@Component({
  // eslint-disable-next-line @angular-eslint/component-selector -- A native tbody keeps valid table and accessibility semantics.
  selector: 'tbody[appRemoteHostCard]',
  standalone: true,
  imports: [
    DatePipe,
    AppTooltipDirective,
    CapabilityHealthComponent,
    GitTokenCapabilityComponent,
    HostWorkloadSummaryComponent,
    HostTelemetryHistoryComponent,
    RuntimeCapacityEditorComponent,
    RemoteHostRoleRowComponent,
    RemoteHostDetailSummaryComponent,
  ],
  templateUrl: './remote-host-card.html',
  styleUrl: './remote-host-card.scss',
  host: {
    'data-testid': 'remote-host-card',
    '[attr.data-tone]': 'tone()',
    '[attr.data-host]': 'host().id',
    '[attr.data-retired]': 'retired()',
  },
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RemoteHostCardComponent {
  private readonly codexSignIn = inject(CodexSignInDialogService);
  readonly host = input.required<RemoteHost>();
  readonly roles = input<readonly RemoteHost[]>([]);
  readonly roleActiveSlots = input<Readonly<Record<string, number>>>({});
  /** Board-local process runs or remote leased runs attributed to this host. */
  readonly boardActiveSlots = input(0);
  /**
   * Which projects hold this host's slots. Supplied by the panel from the same
   * board lease truth as {@link boardActiveSlots}, so the per-project rows and
   * the active-slot total always reconcile.
   */
  readonly projectSlots = input<readonly HostProjectSlots[]>([]);
  readonly expanded = input(false);
  /** Injected clock so the relative heartbeat label ticks without a per-card timer. */
  readonly now = input<number>(Date.now());
  readonly action = output<{ kind: HostActionKind; id: string }>();
  readonly capacityChange = output<RuntimeCapacityChange>();
  readonly projectPolicyChange = output<HostProjectPolicyChange>();
  readonly setup = output<RemoteHost>();
  readonly expandedChange = output<boolean>();
  readonly expandedSections = signal<readonly DetailSection[]>([]);

  readonly liveLoading = computed(() => this.host().liveDataState === 'loading');
  readonly liveError = computed(() => this.host().liveDataState === 'error');
  readonly tone = computed(() => this.liveLoading() ? 'idle' : hostStatusTone(this.host().status));
  readonly statusLabel = computed(() => this.liveLoading() ? 'Loading live status' : hostStatusLabel(this.host().status));
  readonly roleLabel = computed(() => hostRoleLabel(this.host().role));
  readonly heartbeatLabel = computed(() => this.liveLoading()
    ? 'loading…'
    : relativeHeartbeat(this.host().lastHeartbeatAt, this.now()));
  readonly retired = computed(() => this.host().status === 'retired');
  readonly stale = computed(() => !this.liveLoading() && hostIsStale(this.host().lastHeartbeatAt, this.now()));
  readonly latestTelemetry = computed(() => latestHostTelemetry(this.host()));
  readonly liveTelemetry = computed(() => freshHostTelemetry(this.host(), this.now()));
  readonly telemetryStale = computed(() =>
    this.latestTelemetry() !== null && this.liveTelemetry() === null);
  readonly taskServerRouteLabel = computed(() => {
    switch (taskServerRouteStatus(this.host())) {
      case 'reachable': return 'reachable';
      case 'degraded': return 'degraded';
      case 'unreachable': return 'unreachable';
      case 'unknown': return 'not reported';
    }
  });

  readonly meters = computed<Meter[]>(() => {
    const h = this.host();
    const s = h.stats;
    if (!s || this.stale()) return [];
    const ram = ramUsedPct(s) ?? 0;
    const disk = diskUsedPct(s) ?? 0;
    const load = Math.round(clampPct(s.cpuLoadPct));
    return [
      {
        key: 'ram',
        label: 'RAM',
        detail: `${formatMemory(s.ramTotalMb - s.ramFreeMb)} / ${formatMemory(s.ramTotalMb)}`,
        pct: ram,
        tone: meterTone(ram),
      },
      {
        key: 'cpu',
        label: 'CPU',
        detail: `${s.cpuCores} cores · ${s.cpuModel}`,
        pct: load,
        tone: meterTone(load),
      },
      ...(s.diskTotalGb > 0 ? [{
        key: 'disk',
        label: 'Disk',
        detail: `${formatDisk(s.diskTotalGb - s.diskFreeGb)} / ${formatDisk(s.diskTotalGb)}`,
        pct: disk,
        tone: meterTone(disk),
      } as Meter] : []),
    ];
  });

  readonly taskInflowLabel = computed(() => {
    const host = this.host();
    if (this.liveLoading()) return 'loading…';
    if (this.liveError()) return 'unknown';
    if (this.retired()) return 'retired';
    if (host.status === 'draining') return 'blocked';
    return this.stale() ? 'unknown' : 'open';
  });
  readonly daemonLabel = computed(() => {
    if (this.liveLoading()) return 'loading live status…';
    if (this.liveError()) return 'status unavailable';
    return this.stale() ? 'stopped' : (this.host().daemonState ?? 'running');
  });
  readonly runSlotsLabel = computed(() => {
    if (this.liveLoading()) return 'Loading daemon telemetry…';
    if (this.liveError()) return 'Live count unavailable';
    const latest = this.latestTelemetry();
    if (!latest) return 'No slot telemetry';
    if (!this.liveTelemetry()) return `${latest.activeSlots} active · stale`;
    return `${latest.activeSlots} active`;
  });
  readonly runSlotsDiverge = computed(() => {
    const telemetry = this.liveTelemetry();
    return telemetry !== null && telemetry.activeSlots !== this.boardActiveSlots();
  });
  readonly runSlotsTooltip = computed(() => {
    const telemetry = this.liveTelemetry();
    if (!telemetry) return 'Active-slot telemetry is stale or unavailable.';
    if (this.runSlotsDiverge()) {
      const boardSource = this.host().role === 'remote' ? 'live remote leases' : 'live local executions';
      return `Sources disagree: telemetry reports ${telemetry.activeSlots} active slots; the board reports ${this.boardActiveSlots()} ${boardSource} for this host.`;
    }
    return `Telemetry and board leases agree on ${telemetry.activeSlots} active ${telemetry.activeSlots === 1 ? 'slot' : 'slots'}.`;
  });
  readonly failedProjectPreflights = computed(() =>
    (this.host().projectPreflights ?? []).filter(preflight => preflight.status === 'failed'),
  );
  readonly providerAuthBadges = computed(() => providerAuthBadgesForHost(this.host(), this.now()));
  readonly slotTotal = computed(() => roleSlotTotal(this.host()));
  readonly roleSlotCapacity = computed(() => {
    const totals = this.roles().map(roleSlotTotal).filter((value): value is number => value !== null);
    return totals.length === this.roles().length
      ? totals.reduce((total, value) => total + value, 0)
      : null;
  });
  readonly occupiedSlots = computed(() => this.boardActiveSlots());
  readonly loadPct = computed(() => {
    if (this.liveLoading() || this.liveError() || this.stale()) return null;
    const load = this.host().stats?.cpuLoadPct;
    return load === null || load === undefined ? null : Math.round(clampPct(load));
  });
  readonly loadLabel = computed(() => this.loadPct() === null ? null : `${this.loadPct()}%`);
  readonly releaseLabel = computed(() => this.host().releaseId?.trim() || null);
  readonly detailId = computed(() => `remote-host-detail-${this.host().id.replace(/[^a-zA-Z0-9_-]/g, '-')}`);
  readonly healthyCapabilityCount = computed(() => {
    const health = this.host().capabilityHealth ?? [];
    if (!health.length) return this.host().capabilities.length;
    return health.filter(capability => capability.isFresh
      && capability.healthState === 'healthy'
      && capability.advertisedStatus === 'ready').length;
  });
  readonly unavailableAuthCount = computed(() =>
    this.providerAuthBadges().filter(auth => auth.state === 'unavailable').length);
  readonly totalActiveSlots = computed(() => this.roles()
    .reduce((total, role) => total + this.activeSlotsFor(role), 0));
  readonly identitySummary = computed(() => {
    const count = this.roles().length || 1;
    return `${count} ${count === 1 ? 'role' : 'roles'} · ${this.host().os}`;
  });
  readonly connectionSummary = computed(() =>
    `${this.daemonLabel()} · Task Server ${this.taskServerRouteLabel()} · ${this.heartbeatLabel()}`);
  readonly capabilitySummary = computed(() => {
    const ok = this.healthyCapabilityCount();
    const unavailable = this.unavailableAuthCount();
    if (unavailable) return `${ok} capabilities ok · ${unavailable} auth unavailable`;
    return `${ok} ${ok === 1 ? 'capability' : 'capabilities'} ok`;
  });
  readonly capacitySummary = computed(() => {
    const total = this.roleSlotCapacity();
    return total === null
      ? `${this.totalActiveSlots()} active · capacity not reported`
      : `${this.totalActiveSlots()} / ${total} role slots active`;
  });
  readonly projectAccessSummary = computed(() => {
    const failed = this.failedProjectPreflights().length;
    if (failed) return `${failed} ${failed === 1 ? 'project block' : 'project blocks'}`;
    const policy = this.host().projectPolicy;
    if (!policy || policy.allowAllProjects) return 'All registered projects allowed';
    return `${policy.allowedProjectIds.length} selected projects allowed`;
  });
  readonly hostWorkSummary = computed(() => this.runSlotsLabel());
  readonly systemLoadSummary = computed(() => this.loadLabel() ?? 'System load not reported');

  latestAuthTransition(badge: ProviderAuthBadge) {
    return badge.history.at(-1) ?? null;
  }

  canSignInCodex(badge: ProviderAuthBadge): boolean {
    return badge.provider === 'codex' && ['unavailable', 'expiring'].includes(badge.state);
  }

  openCodexSignIn(badge: ProviderAuthBadge): void {
    this.codexSignIn.open(signInTarget(badge, this.host().address));
  }

  cliIcon(t: CliType): string { return cliTypeIcon(t); }
  cliLabel(t: CliType): string { return cliTypeLabel(t); }
  quotaTone(pct: number | null): MeterTone { return meterTone(pct); }

  toggleExpanded(): void {
    this.expandedChange.emit(!this.expanded());
  }

  emit(kind: HostActionKind): void {
    if (this.host().busyAction) return;
    this.action.emit({ kind, id: this.host().id });
  }

  activeSlotsFor(role: RemoteHost): number {
    return this.roleActiveSlots()[role.id] ?? 0;
  }

  isSectionExpanded(section: DetailSection): boolean {
    return this.expandedSections().includes(section);
  }

  toggleSection(section: DetailSection): void {
    this.expandedSections.update(sections => sections.includes(section)
      ? sections.filter(item => item !== section)
      : [...sections, section]);
  }

  requestSetup(): void {
    const host = this.host();
    if (host.role !== 'remote' || host.status === 'retired' || host.busyAction) return;
    this.setup.emit(host);
  }

}

export type DetailSection =
  | 'identity'
  | 'connection'
  | 'capabilities'
  | 'capacity'
  | 'projects'
  | 'work'
  | 'load';
