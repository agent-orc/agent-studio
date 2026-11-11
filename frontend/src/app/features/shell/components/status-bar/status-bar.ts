import {
  ChangeDetectionStrategy,
  Component,
  OnDestroy,
  OnInit,
  ViewEncapsulation,
  computed,
  DestroyRef,
  inject,
  input,
  output,
} from '@angular/core';
import { TaskService } from '../../../../services/task.service';
import {
  clearVisibleInterval,
  setVisibleInterval,
  type VisibleIntervalHandle,
} from '../../../../utils/visible-interval';
import { UsageCockpitService, UsageAlarmStatusComponent, buildCliChipView, buildCostChipView } from '../../../usage-cockpit';
import type { UsageCli } from '../../../usage-cockpit';
import { ProviderAuthStatusService } from '../../../remote-hosts/services/provider-auth-status.service';
import {
  claimLoadGateLabel,
  deriveBoardRunningTruth,
  hostsNotClaiming,
  notClaimingLabel,
  RemoteHostsService,
  ReviewQueueService,
} from '../../../remote-hosts';

import { StatusbarItemComponent } from '../statusbar-item/statusbar-item.component';
import { CrashRecoveryPromptComponent } from '../crash-recovery-prompt/crash-recovery-prompt.component';
import { summarizeStatusBarHostLoad, summarizeStatusBarSlotsByRole } from './status-bar-host-load';
import { describeStatusBarReviewLane, summarizeStatusBarReviewLane } from './status-bar-review-lane';
import { withRouteSegment } from '../../../../services/url-hash.util';

const HOST_LOAD_REFRESH_MS = 30_000;

export function formatRunningLabel(
  local: number,
  remote: number,
  codingSlotCeiling: number | null = null,
  connectedRemoteHosts = 0,
): string {
  const hasRemotePlane = remote > 0 || connectedRemoteHosts > 0;
  const remoteLabel = remote > 0
    ? `remote ${remote}${codingSlotCeiling !== null ? `/${codingSlotCeiling}` : ''}`
    : 'remote idle';
  if (local > 0) return hasRemotePlane ? `${local} local · ${remoteLabel}` : `${local} local`;
  return hasRemotePlane ? remoteLabel : 'local idle';
}

@Component({
  selector: 'app-status-bar',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  encapsulation: ViewEncapsulation.None,
  imports: [UsageAlarmStatusComponent, StatusbarItemComponent, CrashRecoveryPromptComponent],
  templateUrl: './status-bar.html',
  styleUrl: './status-bar.scss',
})
export class StatusBarComponent implements OnInit, OnDestroy {
  private readonly jobService = inject(TaskService);
  private readonly remoteHosts = inject(RemoteHostsService);
  private readonly reviewQueue = inject(ReviewQueueService);
  readonly usage = inject(UsageCockpitService);
  private readonly providerAuth = inject(ProviderAuthStatusService);
  private hostLoadRefreshHandle: VisibleIntervalHandle | null = null;
  constructor() { this.usage.connect(inject(DestroyRef)); }

  readonly visibleUsageClis = computed(() => (this.usage.snapshot()?.clis ?? []).filter(cli =>
    cli.windows.some(window => window.usedPct !== null)
    || (this.usage.snapshot()?.runs ?? []).some(run => run.effectiveCli === cli.cliId)));
  usageView(cli: UsageCli) {
    return buildCliChipView(cli.cliId, cli, this.usage.snapshot()?.timeZone ?? null, Date.now(), [], [], this.usage.readFailed());
  }
  costView() { return buildCostChipView(this.usage.snapshot()?.cost ?? null, Date.now(), [], this.usage.readFailed()); }
  age(iso: string | null | undefined): string {
    const elapsed = Date.now() - Date.parse(iso ?? '');
    if (!Number.isFinite(elapsed) || elapsed < 0) return 'age unknown';
    if (elapsed < 60_000) return 'less than 1 min old';
    if (elapsed < 3_600_000) return `${Math.floor(elapsed / 60_000)} min old`;
    if (elapsed < 86_400_000) return `${Math.floor(elapsed / 3_600_000)} h old`;
    return `${Math.floor(elapsed / 86_400_000)} d old`;
  }
  usageTooltip(cli: UsageCli): string {
    const view = this.usageView(cli);
    return `${view.detail}\n${view.state === 'stale' ? 'Refresh the usage source or check the provider connection in Execution Hosts.' : 'Open usage detail.'}`;
  }
  readonly hostProblems = computed(() => {
    const problems = new Map<string, string[]>();
    for (const badge of this.providerAuth.statuses()) {
      if (!badge.reachable || badge.state !== 'unavailable' || badge.healthOutcome !== 'credential_invalid') continue;
      const names = problems.get(badge.provider) ?? [];
      if (!names.includes(badge.hostName)) names.push(badge.hostName);
      problems.set(badge.provider, names);
    }
    return problems;
  });
  hostProblem(cliId: string): string | null {
    const names = this.hostProblems().get(cliId);
    return names?.length ? `sign-in needed on ${names.join(', ')}` : null;
  }

  readonly projectNames = input<string[]>([]);

  readonly openUsageDetail = output<{ section: 'cli' | 'cost'; cliId: string | null }>();

  readonly runningTruth = computed(() =>
    deriveBoardRunningTruth(this.jobService.grouped().progress));

  /**
   * Active execution slots and configured ceilings split by executor plane
   * (coding vs review, AGT-2645). Coding and review daemons register as
   * separate RunnerIds, so this is a read of the existing execution-host
   * registry, not a new data source.
   */
  readonly slotsByRole = computed(() => summarizeStatusBarSlotsByRole(this.remoteHosts.hosts()));

  readonly remoteActiveSlots = computed(() => {
    const coding = this.slotsByRole().coding;
    return coding.hasUtilization ? coding.active : this.runningTruth().remote;
  });

  readonly runningCount = computed(() => this.runningTruth().local + this.remoteActiveSlots());

  /** Hosts whose claim gate is closed: free slots there take no new work. */
  readonly notClaiming = computed(() => hostsNotClaiming(this.remoteHosts.hosts()));

  readonly runningLabel = computed(() => {
    const truth = this.runningTruth();
    const running = formatRunningLabel(
      truth.local,
      this.remoteActiveSlots(),
      this.slotsByRole().coding.ceiling,
      this.slotsByRole().coding.hostCount,
    );
    const notClaiming = this.notClaiming().length;
    return notClaiming ? `${running} · ${notClaimingLabel(notClaiming)}` : running;
  });
  readonly runningSourcesDiverge = computed(() => {
    const coding = this.slotsByRole().coding;
    if (!coding.hasUtilization) return false;
    const telemetry = coding.active;
    const boardRemote = this.runningTruth().remote;
    // Only flag when board shows MORE coding runs than telemetry reports active
    // slots (a runner is working but not advertising). Telemetry > board is
    // tolerated while the board projection catches up with runner telemetry.
    return telemetry < boardRemote;
  });

  readonly hostLoad = computed(() =>
    summarizeStatusBarHostLoad(this.remoteHosts.hosts(), this.runningCount()));

  readonly reviewSnapshot = computed(() => this.reviewQueue.snapshot());

  /** Active post-processing jobs (LLM orchestration workers running). */
  readonly reviewActiveJobs = computed(() => this.reviewSnapshot()?.activeJobs ?? 0);

  /** Live review runner occupancy, with the queue snapshot as a compatibility fallback. */
  readonly reviewActiveSlots = computed(() => {
    const review = this.slotsByRole().review;
    return review.hasUtilization ? review.active : this.reviewActiveJobs();
  });

  /**
   * AGT-3589: Post Processing cards as the board draws them. The auto-review
   * queue snapshot only counts cards already in the local slot queue and read
   * "review idle" while the lane held ten waiting cards.
   */
  readonly reviewLane = computed(() =>
    summarizeStatusBarReviewLane(this.jobService.grouped().autoReview ?? []));

  /** Cards needing a review attempt or an operator decision. */
  readonly reviewWaiting = computed(() => {
    const lane = this.reviewLane();
    return lane.waiting + lane.stalled;
  });

  /**
   * ATTENTION: cards are queued but nothing is draining. The operator sees an
   * amber signal so a silent stuck queue is never read as "idle and fine".
   */
  readonly reviewAttention = computed(() =>
    this.reviewWaiting() > 0 && this.reviewActiveSlots() === 0);

  /**
   * One-line label for the review plane status-bar item. Appends "/ceiling"
   * (AGT-2645, e.g. "review 2/6") once the review plane's own configured slot
   * ceiling is known, so utilization reads at a glance next to the coding
   * plane's figure; falls back to a bare active count when no review host has
   * reported a ceiling yet.
   */
  readonly reviewLabel = computed(() => {
    const active = this.reviewActiveSlots();
    const waiting = this.reviewWaiting();
    const integration = this.reviewLane().integration;
    const integrationLabel = integration > 0 ? ` · ${integration} awaiting integration` : '';
    if (active === 0 && waiting === 0) return `review idle${integrationLabel}`;
    const ceiling = this.slotsByRole().review.ceiling;
    const activeLabel = ceiling !== null ? `${active}/${ceiling}` : `${active}`;
    if (waiting > 0) return `review ${activeLabel} · ${waiting} waiting${integrationLabel}`;
    return `review ${activeLabel}${integrationLabel}`;
  });

  /** Tooltip for the review plane item. */
  readonly reviewTooltip = computed(() => {
    const snap = this.reviewSnapshot();
    const base = `${describeStatusBarReviewLane(this.reviewLane())} `
      + this.planeHostDetail('Review', this.slotsByRole().review);
    if (snap?.isStagnant) {
      return `${base} Warning: the review queue has not drained for >${snap.stagnantThresholdMinutes} minutes.`;
    }
    return base;
  });

  readonly autoCount = computed(() => {
    const status = this.jobService.runnerStatus();
    return Object.values(status.projects).filter(
      p => p.mode === 'auto-continuous' || p.mode === 'auto-single'
    ).length;
  });

  readonly latestCliRepair = computed(() => {
    const repairs = (this.jobService.runnerStatus().cliRepairs ?? [])
      .filter(item => item.outcome === 'failed'
        || item.outcome === 'attempting'
        || item.outcome === 'detected');
    return repairs.reduce<(typeof repairs)[number] | null>((latest, item) =>
      latest === null || Date.parse(item.occurredAt) > Date.parse(latest.occurredAt) ? item : latest,
    null);
  });

  readonly cliRepairLabel = computed(() => {
    const repair = this.latestCliRepair();
    if (!repair) return '';
    const parsed = Date.parse(repair.occurredAt);
    const time = Number.isFinite(parsed)
      ? new Intl.DateTimeFormat('en-US', { hour: '2-digit', minute: '2-digit' })
        .format(new Date(parsed))
      : 'unknown time';
    if (repair.outcome === 'attempting') return `CLI repair started at ${time}`;
    return repair.outcome === 'detected'
      ? `CLI broken since ${time}`
      : `CLI repair failed at ${time}`;
  });

  /** Screen-reader label for the same two acute outcomes the tone encodes. */
  readonly cliRepairWarningLabel = computed(() =>
    this.latestCliRepair()?.outcome === 'detected'
      ? 'CLI broken'
      : 'CLI repair failed');

  readonly projectCount = computed(() => this.projectNames().length || Object.keys(this.jobService.runnerStatus().projects).length);

  readonly activeFallback = computed(() => {
    for (const project of Object.values(this.jobService.runnerStatus().projects)) {
      if (project.activeJobId && project.quotaFallbackModel) return project;
    }
    return null;
  });

  ngOnInit(): void {
    this.remoteHosts.refresh();
    this.reviewQueue.refresh();
    this.hostLoadRefreshHandle = setVisibleInterval(
      () => { this.remoteHosts.refresh(); this.reviewQueue.refresh(); },
      HOST_LOAD_REFRESH_MS,
    );
  }

  ngOnDestroy(): void {
    clearVisibleInterval(this.hostLoadRefreshHandle);
  }

  runningTooltip(): string {
    const notClaiming = this.notClaiming();
    if (!notClaiming.length) return `Open execution hosts. ${this.runningDetail()}`;
    const reasons = notClaiming.map(host => `${host.name}: ${claimLoadGateLabel(host.claimLoadGate!)}.`).join(' ');
    return `Open ${notClaiming[0].name} in Execution Hosts. ${reasons} Running work continues. ${this.runningDetail()}`;
  }

  private runningDetail(): string {
    const truth = this.runningTruth();
    const coding = this.slotsByRole().coding;
    const execution = `Running ${truth.local + this.remoteActiveSlots()} - ${truth.local} local / ${this.remoteActiveSlots()} remote coding slots.`;
    const comparison = !coding.hasUtilization
      ? ' Fresh remote slot telemetry is unavailable.'
      : this.runningSourcesDiverge()
        ? ` Warning: Board leases report ${truth.remote} remote but host telemetry only reports ${coding.active} active slots; a runner may not be advertising.`
        : coding.active === truth.remote
          ? ` Board leases and host telemetry agree on ${truth.remote} remote ${truth.remote === 1 ? 'run' : 'runs'}.`
          : ` Host telemetry reports ${coding.active} active remote coding slots while the board reports ${truth.remote} remote runs.`;
    const hostDetail = this.planeHostDetail('Remote coding', coding);
    const load = this.hostLoad();
    if (!load) return `${execution}${comparison} ${hostDetail} Execution host load is unavailable.`;

    const loadDetail = `Execution host load ${load.load1.toFixed(1)} / ${load.cpuCores} cores `
      + `(${Math.round(load.ratio * 100)}%); ${load.activeSlots} active execution `
      + `${load.activeSlots === 1 ? 'slot' : 'slots'}.`;
    if (load.correlation === 'load-without-runs') {
      return `${execution}${comparison} ${hostDetail} ${loadDetail} Quiet consistency hint: host load is elevated without reported runs.`;
    }
    if (load.correlation === 'runs-without-load') {
      return `${execution}${comparison} ${hostDetail} ${loadDetail} Quiet consistency hint: reported runs and host load may not correspond.`;
    }
    return `${execution}${comparison} ${hostDetail} ${loadDetail}`;
  }

  autoTooltip(): string {
    return `${this.autoCount()} of ${this.projectCount()} project(s) have auto-pickup enabled.`;
  }

  private planeHostDetail(
    label: string,
    plane: ReturnType<typeof summarizeStatusBarSlotsByRole>['coding'],
  ): string {
    if (plane.hostCount === 0) return `${label} host detail is unavailable.`;
    const count = `${plane.hostCount} ${plane.hostCount === 1 ? 'host' : 'hosts'} connected`;
    const hosts = plane.hosts.map(host => {
      const utilization = host.active === null
        ? 'slot usage unavailable'
        : host.ceiling === null
          ? `${host.active} busy`
          : `${host.active}/${host.ceiling} slots busy`;
      return `${host.name}: ${utilization}`;
    }).join('; ');
    return `${label}: ${count}. ${hosts}.`;
  }

  navigateToExecutionHosts(): void {
    window.location.hash = withRouteSegment(
      window.location.hash,
      '/workspace/settings/environment',
    );
  }

  /** The runner entry opens the longest-closed host that is not claiming, else the host list. */
  openRunnerEntry(): void {
    const host = this.notClaiming()[0];
    if (!host) {
      this.navigateToExecutionHosts();
      return;
    }
    window.location.hash = withRouteSegment(
      window.location.hash,
      `/workspace/settings/environment?host=${encodeURIComponent(host.id)}`,
    );
  }

}
