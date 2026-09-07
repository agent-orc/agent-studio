import {
  ChangeDetectionStrategy,
  Component,
  OnDestroy,
  OnInit,
  ViewEncapsulation,
  computed,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { TaskService } from '../../../../services/task.service';
import { ClientDefaultsService } from '../../../../services/client-defaults.service';
import { NowTickService } from '../../../../services/now-tick.service';
import { formatDateTimeUtc } from '../../../../services/format.util';
import type { CliType } from '../../../../models/task.model';
import { CLI_TYPES } from '../../../../models/task.model';
import {
  clearVisibleInterval,
  setVisibleInterval,
  type VisibleIntervalHandle,
} from '../../../../utils/visible-interval';
import { UsageHoverPanelComponent } from '../../../tokens';
import {
  deriveBoardRunningTruth,
  RemoteHostsService,
  ReviewQueueService,
} from '../../../remote-hosts';

import { StatusbarItemComponent } from '../statusbar-item/statusbar-item.component';
import { CliModelSelectorComponent } from '../../../../components/cli-model-selector';
import { summarizeStatusBarHostLoad, summarizeStatusBarSlotsByRole } from './status-bar-host-load';
import { withRouteSegment } from '../../../../services/url-hash.util';

const STORAGE_DEFAULT_CLI = 'defaultCliType';
const STORAGE_DEFAULT_MODEL_PREFIX = 'defaultModel:';
const STORAGE_DEFAULT_THINKING_PREFIX = 'defaultThinkingLevel:';
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
  return hasRemotePlane ? remoteLabel : 'no runners';
}

/**
 * AGT-2726 — the board's "git state as of" label. Git-derived card enrichment
 * comes from a background index now, so the board can be honest about its age
 * instead of implying the numbers were computed for this request. Deliberately
 * seconds-resolution: the acceptance bound is an index age under 10 s after a
 * ref change, and a label that only speaks in minutes could not show that.
 */
export function formatGitStateLabel(
  gitStateAt: string | null | undefined,
  stale: boolean,
  now: number,
): string {
  if (!gitStateAt) return stale ? 'git indexing' : 'git idle';
  const at = new Date(gitStateAt).getTime();
  if (Number.isNaN(at)) return 'git idle';
  const seconds = Math.max(0, Math.round((now - at) / 1000));
  const age = seconds < 60
    ? `${seconds}s`
    : seconds < 3600
      ? `${Math.round(seconds / 60)}m`
      : `${Math.round(seconds / 3600)}h`;
  return stale ? `git ${age} · refreshing` : `git ${age}`;
}

@Component({
  selector: 'app-status-bar',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  encapsulation: ViewEncapsulation.None,
  imports: [UsageHoverPanelComponent, StatusbarItemComponent, CliModelSelectorComponent],
  templateUrl: './status-bar.html',
  styleUrl: './status-bar.scss',
})
export class StatusBarComponent implements OnInit, OnDestroy {
  private readonly jobService = inject(TaskService);
  private readonly clientDefaults = inject(ClientDefaultsService);
  private readonly remoteHosts = inject(RemoteHostsService);
  private readonly reviewQueue = inject(ReviewQueueService);
  private readonly nowTick = inject(NowTickService);
  private hostLoadRefreshHandle: VisibleIntervalHandle | null = null;

  readonly projectNames = input<string[]>([]);

  // Open-state of each overlay this bar can toggle. Bound to the panel's
  // own `xOpen` signal by the shell so the trigger button shows a
  // pressed/active state while its panel is visible (and `aria-pressed`
  // reflects it). The bar stays presentational — the source of truth for
  // "is the panel open" lives with the panel, not here.
  readonly usageOpen = input(false);
  readonly orchestratorOpen = input(false);
  readonly orchestratorActiveChatCount = input(0);
  readonly feedOpen = input(false);
  readonly settingsOpen = input(false);
  readonly showSignOut = input(false);
  readonly signedInLabel = input('');

  readonly toggleUsage = output<void>();
  readonly toggleOrchestrator = output<void>();
  readonly toggleFeed = output<void>();
  // Single entry into the global Workspace-settings home. Summary, visual
  // evidence and CLI management are sections of that home now, so the
  // status bar exposes one button instead of three scattered ones.
  readonly toggleSettings = output<void>();
  readonly signOut = output<void>();
  // Quota-strip click: open the CLI-Management (usage caps) section
  // directly, where the full usage detail lives. Separate from
  // `toggleSettings`, which lands on the home overview.
  readonly openCliAdmin = output<void>();
  readonly defaultCliChange = output<CliType>();
  readonly defaultModelChange = output<{ cliType: CliType; model: string; thinkingLevel: string | null }>();

  readonly defaultCli = signal<CliType>(this.readDefaultCli());
  readonly defaultModel = signal<string>(this.readDefaultModel(this.readDefaultCli()));
  readonly defaultThinkingLevel = signal<string | null>(this.readDefaultThinkingLevel(this.readDefaultCli()));

  readonly runningTruth = computed(() =>
    deriveBoardRunningTruth(this.jobService.grouped().progress));

  /**
   * AGT-2726 — how old the git-derived card enrichment on this board is. It is
   * read from the grouped payload the board already polls, so the stamp costs
   * nothing extra, and it is rendered without a tone or a warning: an index a
   * few seconds behind is the normal, healthy state, not an alarm.
   */
  readonly gitStateLabel = computed(() => formatGitStateLabel(
    this.jobService.grouped().gitStateAt,
    this.jobService.grouped().gitStateStale === true,
    this.nowTick.now()));

  readonly gitStateTooltip = computed(() => {
    const grouped = this.jobService.grouped();
    if (!grouped.gitStateAt) {
      return 'Git-derived card state (merge, integration, publish) is still being indexed in the background.';
    }
    const at = formatDateTimeUtc(grouped.gitStateAt);
    return grouped.gitStateStale
      ? `Git-derived card state as of ${at}. A repository change was seen since; the background index is catching up and the board updates itself.`
      : `Git-derived card state as of ${at}. The background index is up to date.`;
  });

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

  readonly runningLabel = computed(() => {
    const truth = this.runningTruth();
    return formatRunningLabel(
      truth.local,
      this.remoteActiveSlots(),
      this.slotsByRole().coding.ceiling,
      this.slotsByRole().coding.hostCount,
    );
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

  /** Cards waiting in the post-processing queue but not yet running. */
  readonly reviewWaiting = computed(() => this.reviewSnapshot()?.queueDepth ?? 0);

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
    if (active === 0 && waiting === 0) return 'review idle';
    const ceiling = this.slotsByRole().review.ceiling;
    const activeLabel = ceiling !== null ? `${active}/${ceiling}` : `${active}`;
    if (waiting > 0) return `review ${activeLabel} · ${waiting} waiting`;
    return `review ${activeLabel}`;
  });

  /** Tooltip for the review plane item. */
  readonly reviewTooltip = computed(() => {
    const snap = this.reviewSnapshot();
    const slotDetail = this.planeHostDetail('Review', this.slotsByRole().review);
    if (!snap) return `Auto-review queue data unavailable. ${slotDetail}`;
    const { activeJobs, queueDepth, isStagnant, stagnantThresholdMinutes } = snap;
    const base = `Auto-review queue: ${activeJobs} processing, ${queueDepth} waiting. ${slotDetail}`;
    if (isStagnant) return `${base} Warning: queue has not drained for >${stagnantThresholdMinutes} minutes.`;
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
      .filter(item => item.outcome === 'failed' || item.outcome === 'attempting');
    return repairs.reduce<(typeof repairs)[number] | null>((latest, item) =>
      latest === null || Date.parse(item.occurredAt) > Date.parse(latest.occurredAt) ? item : latest,
    null);
  });

  readonly cliRepairLabel = computed(() => {
    const repair = this.latestCliRepair();
    if (!repair) return '';
    const parsed = Date.parse(repair.occurredAt);
    const time = Number.isFinite(parsed)
      ? new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit' })
        .format(new Date(parsed))
      : 'unknown time';
    return repair.outcome === 'attempting'
      ? `CLI repair started at ${time}`
      : `CLI repair failed at ${time}`;
  });

  readonly projectCount = computed(() => this.projectNames().length || Object.keys(this.jobService.runnerStatus().projects).length);
  readonly orchestratorLabel = computed(() => this.orchestratorActiveChatCount() > 0
    ? `Orchestrator · ${this.orchestratorActiveChatCount()} active`
    : 'Orchestrator');
  readonly orchestratorTooltip = computed(() => this.orchestratorActiveChatCount() > 0
    ? `${this.orchestratorActiveChatCount()} orchestrator chat(s) are working. Open Orchestrator Chat.`
    : 'Orchestrator chat');

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
    void this.clientDefaults.hydrate().then(() => {
      const cli = this.readDefaultCli();
      this.defaultCli.set(cli);
      this.defaultModel.set(this.readDefaultModel(cli));
      this.defaultThinkingLevel.set(this.readDefaultThinkingLevel(cli));
    });
  }

  ngOnDestroy(): void {
    clearVisibleInterval(this.hostLoadRefreshHandle);
  }

  runningTooltip(): string {
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
    if (!load) return `Open execution hosts. ${execution}${comparison} ${hostDetail} Execution host load is unavailable.`;

    const loadDetail = `Execution host load ${load.load1.toFixed(1)} / ${load.cpuCores} cores `
      + `(${Math.round(load.ratio * 100)}%); ${load.activeSlots} active execution `
      + `${load.activeSlots === 1 ? 'slot' : 'slots'}.`;
    if (load.correlation === 'load-without-runs') {
      return `Open execution hosts. ${execution}${comparison} ${hostDetail} ${loadDetail} Quiet consistency hint: host load is elevated without reported runs.`;
    }
    if (load.correlation === 'runs-without-load') {
      return `Open execution hosts. ${execution}${comparison} ${hostDetail} ${loadDetail} Quiet consistency hint: reported runs and host load may not correspond.`;
    }
    return `Open execution hosts. ${execution}${comparison} ${hostDetail} ${loadDetail}`;
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
      '/workspace/settings/execution-hosts',
    );
  }

  /** Atomic commit from the unified selector. Persists to localStorage and
   *  to the cross-device ClientDefaults profile; the create-task form
   *  subscribes via `defaultCliChange` / `defaultModelChange`. */
  onDefaultCommit(change: { cliType: CliType; model: string; thinkingLevel: string | null }): void {
    const previousCli = this.defaultCli();
    if (change.cliType !== previousCli) {
      this.defaultCli.set(change.cliType);
      localStorage.setItem(STORAGE_DEFAULT_CLI, change.cliType);
      void this.clientDefaults.pushDefaultCli(change.cliType);
      this.defaultCliChange.emit(change.cliType);
      // When the CLI flips we also need a fresh per-CLI model preference.
      this.defaultModel.set(this.readDefaultModel(change.cliType));
      this.defaultThinkingLevel.set(this.readDefaultThinkingLevel(change.cliType));
    }
    const model = change.model;
    const thinkingLevel = change.thinkingLevel;
    if (model !== this.defaultModel() || thinkingLevel !== this.defaultThinkingLevel() || change.cliType !== previousCli) {
      this.defaultModel.set(model);
      this.defaultThinkingLevel.set(thinkingLevel);
      if (model) {
        localStorage.setItem(STORAGE_DEFAULT_MODEL_PREFIX + change.cliType, model);
      } else {
        localStorage.removeItem(STORAGE_DEFAULT_MODEL_PREFIX + change.cliType);
      }
      if (thinkingLevel) {
        localStorage.setItem(STORAGE_DEFAULT_THINKING_PREFIX + change.cliType, thinkingLevel);
      } else {
        localStorage.removeItem(STORAGE_DEFAULT_THINKING_PREFIX + change.cliType);
      }
      void this.clientDefaults.pushDefaultModel(model);
      void this.clientDefaults.pushDefaultThinkingLevel(thinkingLevel);
      this.defaultModelChange.emit({ cliType: change.cliType, model, thinkingLevel });
    }
  }

  private readDefaultCli(): CliType {
    const stored = localStorage.getItem(STORAGE_DEFAULT_CLI) as CliType | null;
    if (stored && (CLI_TYPES as string[]).includes(stored)) return stored;
    return 'claude';
  }

  private readDefaultModel(cliType: CliType): string {
    return localStorage.getItem(STORAGE_DEFAULT_MODEL_PREFIX + cliType) ?? '';
  }

  private readDefaultThinkingLevel(cliType: CliType): string | null {
    return localStorage.getItem(STORAGE_DEFAULT_THINKING_PREFIX + cliType) ?? null;
  }
}
