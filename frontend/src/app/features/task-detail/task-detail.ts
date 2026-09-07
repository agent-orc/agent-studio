import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  HostListener,
  inject,
  input,
  output,
  signal,
  effect,
  OnDestroy,
  ViewChild,
  ViewEncapsulation,
} from '@angular/core';
import { ModalStackService } from '../../services/modal-stack.service';
import { FormsModule } from '@angular/forms';
import type {
  TaskDetail,
  TaskInfo,
  WatchPathEntry,
  CliSettings,
  CliType,
  ReviewEvidenceEntry,
} from '../../models/task.model';
import { boardLanes, CLI_TYPES, TaskState } from '../../models/task.model';
import type { CliModelInfo } from '../../features/cli';
import { TaskService } from '../../services/task.service';
import { CliCatalogStore } from '../cli';
import { ErrorDialogService } from '../../services/error-dialog.service';
import { ClientService } from '../../services/client.service';
import { NowTickService } from '../../services/now-tick.service';
import { LayoutPanesService } from './services/layout-panes.service';
import { TaskArtifactsService } from './services/task-artifacts.service';
import { LanePagerService } from './state/lane-pager.service';
import { TaskSelectionService } from './state/task-selection.service';
import { ClaudeSessionPollService } from '../polling/services/claude-session-poll.service';
import { SessionEventsPollService } from '../polling/services/session-events-poll.service';
import { RunTimelinePollService } from '../polling/services/run-timeline-poll.service';
import { TaskTimelinePollService } from '../polling/services/task-timeline-poll.service';
import { AgentWorkSummaryPollService } from '../polling/services/agent-work-summary-poll.service';
import { PlanPollService } from '../polling/services/plan-poll.service';
import { TaskPipelinePollService } from '../polling/services/task-pipeline-poll.service';
import { ScreenshotsPollService } from '../polling/services/screenshots-poll.service';
import { GitPaneService } from './services/git-pane.service';
import { shouldShowFailureToast } from './services/run-outcome.util';
import { GitPaneComponent } from './components/git-pane/git-pane/git-pane.component';
import { CliOutputPollService } from '../polling/services/cli-output-poll.service';
import { CommandDeckComponent } from './components/command-deck/command-deck.component';
import { PromptPaneComponent, type PromptPaneTabId } from './components/prompt-pane/prompt-pane.component';
import { EpicRollupPaneComponent } from './components/epic-rollup-pane/epic-rollup-pane.component';
import { EpicMembershipBannerComponent } from './components/epic-membership-banner/epic-membership-banner.component';
import { LogOverlayComponent } from './components/log-overlay/log-overlay.component';
import { ProtocolPaneComponent } from './components/protocol-pane/protocol-pane/protocol-pane.component';
import { deriveProtocolVerdict } from './components/protocol-pane/protocol-verdict';
import { classifyLatestActivityOutcome } from './components/agent-outcome.util';
import { EscalationSummaryComponent } from './components/escalation-summary/escalation-summary.component';
import { DetailHeaderComponent } from './components/detail-header/detail-header.component';
import { TaskLiveStatusComponent } from '../../components/task-live-status/task-live-status.component';
import { freshestRunInfo, isTaskRunActive } from '../../services/run-activity.util';
import { PaneToggleBarComponent } from './components/pane-toggle-bar/pane-toggle-bar.component';
import { TriageActionPayload, laneLabelFor } from './state/triage-actions.model';
import { UndoController } from '../../services/undo.service';
import {
  claudeSessionTooltip, cliTypeLabel, formatDate, formatDateTime, formatMultiplier,
  formatRateWindow, formatResetIn, formatTime, formatTokens, gitCommitCount,
  gitToggleTooltip, isCliErrorMessage, rateLimitTooltip, stateLabel,
} from './services/task-detail-formatters';
import { taskDetailShortcutTargetAllowed, taskNavigationOwnsFocus } from './task-detail-keyboard.util';

import { TooltipDirective } from 'coding-agent-chat/shared';
@Component({
  selector: 'app-task-detail, app-job-detail',
  standalone: true,
  imports: [
    FormsModule,
    GitPaneComponent,
    CommandDeckComponent,
    PromptPaneComponent,
    EpicRollupPaneComponent,
    EpicMembershipBannerComponent,
    LogOverlayComponent,
    ProtocolPaneComponent,
    EscalationSummaryComponent,
    DetailHeaderComponent,
    TaskLiveStatusComponent,
    PaneToggleBarComponent,
    TooltipDirective,
  ],
  providers: [
    LayoutPanesService,
    ClaudeSessionPollService,
    SessionEventsPollService,
    RunTimelinePollService,
    TaskTimelinePollService,
    AgentWorkSummaryPollService,
    PlanPollService,
    TaskPipelinePollService,
    ScreenshotsPollService,
    GitPaneService,
    CliOutputPollService,
    TaskArtifactsService,
  ],
  // Cycle 7b: OnPush. The detail panel mounts seven polling services
  // (claude session, session events, run timeline, screenshots,
  // git pane, cli output, hygiene strip) plus the protocol/log/git
  // panes; each poll tick used to trigger a default-CD pass over the
  // whole subtree. Signals already mark themselves dirty, so OnPush
  // prunes the unrelated work without changing behavior.
  changeDetection: ChangeDetectionStrategy.OnPush,
  // Keep styles global to this subtree so the still-inline class rules
  // (.pane*, .detail*, .inspector*, .notes-panel*, .sidebar-card*, …)
  // continue to reach the now-extracted sub-components without having
  // to copy each block into its own .scss. Step 9 (per-component
  // styles) can flip this back to default once all blocks have moved.
  encapsulation: ViewEncapsulation.None,
  templateUrl: './task-detail.html',
  styleUrl: './task-detail.scss',
})
export class TaskDetailComponent implements OnDestroy {
  private jobService = inject(TaskService);
  private catalogStore = inject(CliCatalogStore);
  private errorDialog = inject(ErrorDialogService);
  private clientService = inject(ClientService);
  private undo = inject(UndoController);
  readonly detail = input.required<TaskDetail>();
  readonly defaultThinkingLevel = computed(() =>
    this.clientService.resolve(this.detail().info.ownerClientId).defaultThinkingLevel ?? null
  );
  readonly watchPaths = input<WatchPathEntry[]>([]);
  /** Peers in the same on-disk lane as the current job, in kanban order. */
  readonly lanePeers = input<TaskInfo[]>([]);
  readonly selectedSubTaskId = input<string | null>(null);
  readonly routeDetailTab = input<PromptPaneTabId | null>(null);
  readonly routeInspectorTab = input<'task' | 'activity' | 'protocol' | null>(null);
  /** True while the update-service is mid-update; disables triage actions. */
  readonly mutationsBlocked = input(false);
  readonly back = output<void>();
  readonly fileSaved = output<void>();
  /** Back-jump to the parent epic from the epic-membership banner. The host
   *  routes this through the same getDetail + select flow used to open any
   *  task by id (app.onOpenJobDetailFromSheet). */
  readonly openEpicRequested = output<{ jobId: string; watchPath: string }>();
  readonly openSubTaskRequested = output<{ jobId: string; watchPath: string }>();
  /** Forwarded from the prompt-pane Evidence tab into the existing job
   *  service mutation endpoint; the protocol pane no longer hosts the
   *  panel after the user moved Evidence into the left-pane tab. */
  onEvidenceAcknowledge(payload: { entry: ReviewEvidenceEntry; acknowledged: boolean }): void {
    const job = this.detail().info;
    this.jobService
      .acknowledgeReviewEvidence(job.id, payload.entry.id, payload.acknowledged, job.watchPath)
      .subscribe({
        next: () => this.fileSaved.emit(),
        error: () => {
          /* the panel's own busy state clears on next reviewEvidence emission */
        },
      });
  }
  onEvidenceCreateFollowup(entry: ReviewEvidenceEntry): void {
    const job = this.detail().info;
    this.jobService.createReviewEvidenceFollowup(job.id, entry.id, {}, job.watchPath).subscribe({
      next: () => this.fileSaved.emit(),
      error: () => {
        /* surfaced by the panel's own error toast */
      },
    });
  }
  readonly projectChanged = output<string>();
  readonly deleteRequested = output<void>();
  /** Lane dropdown navigation (ASS-661): parent re-points the pager at the
   *  chosen lane. Navigation-only — the open task is never moved here. */
  readonly navigateLaneRequested = output<string>();
  /** Lane-move requested via the triage panel. Parent runs the API call so
   *  it can advance to the next peer on success. */
  readonly triageMoveRequested = output<{ targetState: string; actionId: string }>();
  /** "Move to top" via the triage panel. */
  readonly triageMoveToTopRequested = output<{ actionId: string }>();
  /** "Delete" via the triage panel (already destructive-confirmed inside). */
  readonly triageDeleteRequested = output<{ actionId: string }>();
  /** "Run now" — start the CLI for a 2-ready job. Parent has runner state. */
  readonly triageStartRequested = output<{ actionId: string }>();
  /** Walk to the next peer in the current lane (j / ↓ / → / Next button). */
  readonly nextInLaneRequested = output<void>();
  /** Walk to the previous peer in the current lane (k / ↑ / ← / Prev button). */
  readonly prevInLaneRequested = output<void>();
  readonly detailTabChange = output<PromptPaneTabId>();
  readonly inspectorTabChange = output<'task' | 'activity' | 'protocol'>();
  /** Lane-pager snapshot state for the header (read-only facades). */
  private readonly lanePager = inject(LanePagerService);
  private readonly jobSelection = inject(TaskSelectionService);
  /**
   * True while the selection is fetching the next/previous task without a
   * warmed prefetch to paint instantly. Drives the header's small loading
   * indicator so pager/cursor steps over not-yet-cached tasks show feedback.
   */
  readonly detailLoading = this.jobSelection.detailLoading;
  /**
   * Pager position for the current job. Returns the 1-based index when
   * the job is still part of the snapshot, or 0 when the job has left
   * the captured iteration (external lane change). The header template
   * renders 0 as "—" so the user sees "— of N" instead of a stale index.
   */
  readonly pagerPosition = computed(() => {
    const snap = this.lanePager.snapshot();
    const currentJobKey = this.detail()?.info.taskKey;
    if (!snap || !currentJobKey) return 0;
    const idx = snap.jobs.findIndex(j => j.taskKey === currentJobKey);
    if (idx < 0) return 0;
    return idx + 1;
  });
  readonly pagerTotal = this.lanePager.total;
  readonly pagerCanPrev = this.lanePager.canPrev;
  readonly pagerCanNext = this.lanePager.canNext;
  readonly pagerLaneLabel = this.lanePager.laneLabel;
  /** Lane the pager iterates (snapshot lane, fallback to the open job's
   *  state). Drives the header's navigation-only lane dropdown. */
  readonly pagerLaneState = computed(
    () => this.lanePager.snapshot()?.lane ?? this.detail().info.state,
  );
  readonly editingPrompt = signal(false);
  // Three-pane layout state + resize handlers — owned by LayoutPanesService
  // (provided locally on this component); fields below re-expose as facades.
  private readonly layout = inject(LayoutPanesService);
  readonly panesVisible = this.layout.panesVisible;
  readonly paneWeights = this.layout.paneWeights;
  readonly maximizedPane = this.layout.maximizedPane;
  readonly paneSplitterDragging = this.layout.paneSplitterDragging;
  /**
   * True when the open card is an escalation the operator must decide on, gating
   * the prominent escalation summary panel. Mirrors the board's human-decision
   * semantics (`buildHumanReviewBadge`): a card in the dedicated `5e-escalated`
   * lane always qualifies, and an escalate-verdict card *parked in* 5-human-review
   * qualifies too (the AGT-1994 case, where everything was merged but the
   * escalation still blocked the delivery). Auto-review escalate verdicts are
   * mid-decision and excluded until the card reaches a human-decision lane.
   */
  readonly isEscalated = computed(() => {
    const info = this.detail().info;
    if (info.state === TaskState.Escalated) return true;
    return info.state === TaskState.HumanReview && info.orchestratorVerdict === 'escalate';
  });
  readonly gitCommitCount = computed(() => gitCommitCount(this.detail().info));
  readonly gitToggleTooltip = computed(() => gitToggleTooltip(this.detail().info));
  // Live Claude session telemetry — owned by ClaudeSessionPollService
  // (5 s poll, started/stopped in response to detail() changes).
  private readonly claudePoll = inject(ClaudeSessionPollService);
  readonly claudeSession = this.claudePoll.session;
  readonly claudeRateLimit = this.claudePoll.rateLimit;

  // Per-job session-event log — drives the "session continued / lost"
  // chip in the protocol pane header. Polled at a slower 10 s cadence
  // because events only flip on start/continue/recovery, not per turn.
  private readonly sessionEventsPoll = inject(SessionEventsPollService);

  // Per-job run timeline (CLI invocations between user inputs). Drives
  // the run-list view in the protocol pane and the per-run commits
  // drill-down. Polled at 5 s; the activity log poll is the source of
  // sub-second tail updates.
  private readonly runTimelinePoll = inject(RunTimelinePollService);

  // Git view state lives in GitPaneService (provided locally on this
  // component). Facades below keep the existing call sites unchanged.
  private readonly git = inject(GitPaneService);
  readonly gitStatus = this.git.status;
  readonly gitLoading = this.git.loading;
  readonly selectedDiffPath = this.git.selectedDiffPath;
  readonly gitDiffText = this.git.diffText;
  readonly commitMessage = this.git.commitMessage;
  readonly committing = this.git.committing;
  readonly generatingMsg = this.git.generatingMsg;
  /** Live-derived landed position of the task's work; null while unknown/loading. */
  readonly landedState = computed(() => this.git.provenance()?.landedState ?? null);
  /**
   * True while the graph-derived git provenance for the open job has not settled
   * yet. Gates the detail-header's git-dependent acceptance primary so it cannot
   * fire (or show a guessed "Merge into Develop" label) before the branch/merge
   * truth is known (AGT-2006). Flips to `false` atomically with `landedState`
   * resolving, so the button switches straight to its true label without a
   * wrong -> right flicker.
   */
  readonly gitInfoLoading = computed(() => !this.git.provenanceLoaded());
  readonly commitActionsAvailable = computed(() => {
    const status = this.git.status();
    return this.git.viewMode() === 'worktree'
      && this.isActiveJob()
      && !!status?.isRepo
      && !status.error
      && status.files.length > 0;
  });
  private readonly cliPoll = inject(CliOutputPollService);
  readonly cliOutput = this.cliPoll.output;
  readonly isRunning = this.cliPoll.isRunning;
  /** AGT-2378: `detail()` is fetched once on open, so its runtime overlay ages
   *  out; read run liveness through the live board entry instead. The board
   *  entry only wins when it is at least as fresh, so a lane move applied here
   *  is not undone by a board push that predates it — see `freshestRunInfo`. */
  readonly liveRunInfo = computed(() =>
    freshestRunInfo(this.detail().info, this.jobService.jobs()));
  /** Includes pipeline pre-steps, between-step ownership, and remote runs (the
   *  CLI output poll only ever sees locally spawned processes). */
  readonly effectiveRunActive = computed(() =>
    this.isRunning() || isTaskRunActive(this.liveRunInfo()));
  readonly startedAt = this.cliPoll.startedAt;
  readonly elapsedTime = this.cliPoll.elapsedTime;
  readonly errorMsg = signal<string | null>(null);
  readonly starting = signal(false);
  readonly continuing = signal(false);
  readonly regeneratingSummary = signal(false);
  private regenPollTimer: ReturnType<typeof setInterval> | null = null;
  private regenStartedAt = 0;
  readonly followupPrompt = signal('');
  readonly queuedFollowUp = signal(false);
  readonly chatError = signal<string | null>(null);
  readonly modelDraft = signal('');
  readonly thinkingLevelDraft = signal<string | null>(null);
  readonly availableModels = signal<CliModelInfo[]>([]);
  readonly cliTypes = CLI_TYPES;
  readonly cliTypeDraft = signal<CliType>('claude');

  modelMultiplier(id: string | null | undefined): number | null {
    if (!id) return null;
    return this.availableModels().find((m) => m.id === id)?.multiplier ?? null;
  }

  formatMultiplier(mult: number | null): string {
    return formatMultiplier(mult);
  }
  readonly showCliConfig = signal(false);
  readonly cliStatus = signal<CliSettings | null>(null);
  readonly cliPathDraft = signal('');
  readonly cliTestResult = signal<CliSettings | null>(null);
  readonly cliTesting = signal(false);
  readonly showLogOverlay = signal(false);
  readonly activeInspectorTab = signal<'task' | 'activity' | 'protocol'>('protocol');
  /** When true while a run is active, the user has manually expanded the
   *  setup bar and we keep it expanded until the run ends or they toggle it
   *  off. Reset on job switch and when the run ends so the next run starts
   *  collapsed again. */
  readonly setupExpandedDuringRun = signal(false);
  /** Effective collapsed state: auto-collapse while running, unless the user
   *  explicitly hit "Show setup". Always expanded when not running. */
  readonly setupCollapsed = computed(() => this.isRunning() && !this.setupExpandedDuringRun());
  readonly tokenDraft = signal('');
  readonly showToken = signal(false);
  readonly tokenSaving = signal(false);
  readonly editingTitle = signal(false);
  readonly titleDraft = signal('');
  readonly savingTitle = signal(false);
  readonly movingToTop = signal(false);
  /** Stable id of the triage button currently in flight (null when idle). */
  readonly triageActingId = signal<string | null>(null);
  readonly detailPanePercent = this.layout.detailPanePercent;

  /** 0-based index of the open job in `lanePeers`. -1 when the parent has not
   *  resolved peers yet (e.g. during the optimistic move grace window). */
  readonly laneIndex = computed(() => {
    const peers = this.lanePeers();
    const key = this.detail().info.taskKey;
    return peers.findIndex((p) => p.taskKey === key);
  });
  readonly laneSize = computed(() => this.lanePeers().length);

  @ViewChild('detailHeader') private detailHeaderRef?: DetailHeaderComponent;

  // Wall-clock tick used by relative-time formatters (e.g. formatResetIn).
  // Sourced from NowTickService — keeps the formatter stable within one
  // change-detection cycle and avoids the NG0100 minute-boundary trap.
  private readonly nowTick = inject(NowTickService).now;

  promptDraftValue = '';
  // Tracks whether the user has explicitly chosen an inspector tab for the
  // current job. Reset on job switch. Used to block the "auto-switch to
  // Protocol once the summary lands" effect from clobbering a manual choice.
  private userTouchedInspectorTab = false;
  private lastCliConfigRequest = 0;
  private currentJobKey: string | null = null;
  // Tracks which failed execution we've already surfaced as a modal so that
  // re-opening the detail view (or the 2s board refresh re-emitting the same
  // failed snapshot) does not re-pop the dialog. Keyed by `${taskKey}|${startedAt}`.
  private lastShownFailureKey: string | null = null;

  constructor() {
    // Load the initial catalog for whatever CLI the current job uses; the effect below
    // will re-trigger this when the user switches CLIs.
    this.loadModelCatalog('claude');
    // Register the detail view as the bottom of the modal stack while it is
    // mounted. Any modal opened on top of it (Add Task, error dialog, verbose
    // debug, confirm-dialog) registers later and therefore wins Escape first.
    // When no modal is on top, Escape closes the detail view itself — except
    // while an inline title/prompt edit or one of the local sub-overlays
    // (log overlay, CLI config) is active; those have their own Escape
    // affordances (template `(keydown.escape)` on the input) and would feel
    // broken if a single Escape jumped past them and closed the panel.
    inject(ModalStackService).pushUntilDestroyed(
      'task-detail',
      () => {
        // Decline (return false) while an inline title/prompt edit or a
        // local sub-overlay is active. The original template binding
        // `(keydown.escape)` on the title input then runs and cancels
        // the edit; closing the whole panel would feel broken.
        if (this.editingTitle() || this.editingPrompt()) return false;
        if (this.showLogOverlay()) {
          this.showLogOverlay.set(false);
          return true;
        }
        if (this.showCliConfig()) {
          this.showCliConfig.set(false);
          return true;
        }
        this.back.emit();
        return true;
      },
      inject(DestroyRef),
    );
  }

  private loadModelCatalog(cliType: CliType) {
    // ADR-0046: synchronous cache hit when the boot-time hydration has
    // already happened (the common case). Falls through to a real fetch
    // on first boot or after an explicit invalidation.
    if (this.catalogStore.hasFresh(cliType)) {
      const models = [...this.catalogStore.modelsFor(cliType)];
      this.availableModels.set(models);
      if (!this.modelDraft()) {
        const def = models.find((m) => m.isDefault);
        if (def) this.modelDraft.set(def.id);
      }
      return;
    }
    this.catalogStore.ensure(cliType).subscribe({
      next: (models) => {
        const list = [...models];
        this.availableModels.set(list);
        if (!this.modelDraft()) {
          const def = list.find((m) => m.isDefault);
          if (def) this.modelDraft.set(def.id);
        }
      },
      error: () => {
        this.availableModels.set([]);
      },
    });
  }

  onCliTypeChange(value: string) {
    if (!CLI_TYPES.includes(value as CliType)) return;
    const next = value as CliType;
    if (next === this.cliTypeDraft()) return;
    this.cliTypeDraft.set(next);
    // Switching CLI clears the previous model — let the user pick one for the new backend.
    this.modelDraft.set('');
    this.loadModelCatalog(next);

    this.jobService
      .setJobCliType(this.detail().info.id, next, this.detail().info.watchPath)
      .subscribe({
        next: () => this.fileSaved.emit(),
        error: (err) => this.showError(err),
      });
  }

  cliTypeLabel(t: CliType): string {
    return cliTypeLabel(t);
  }

  /** When the run ends, drop the user's "Show setup" override so the next run
   *  starts compact again. Idempotent — only writes when the flag would change. */
  private resetSetupExpandWhenIdle = effect(() => {
    if (!this.isRunning() && this.setupExpandedDuringRun()) {
      this.setupExpandedDuringRun.set(false);
    }
  });

  private detailEffect = effect(() => {
    const d = this.detail();
    const isJobSwitch = this.currentJobKey !== d.info.taskKey;
    this.currentJobKey = d.info.taskKey;
    // Keep GitPaneService in sync with the open job; resets internal
    // state on actual job changes, no-ops on same-job refreshes.
    this.git.setJob(d.info);

    this.errorMsg.set(null);
    if (d.info.model) {
      this.modelDraft.set(d.info.model);
    } else {
      const def = this.availableModels().find((m) => m.isDefault);
      this.modelDraft.set(def?.id ?? '');
    }
    this.thinkingLevelDraft.set(d.info.thinkingLevel ?? null);
    const nextCliType = (d.info.cliType ?? 'claude') as CliType;
    if (nextCliType !== this.cliTypeDraft()) {
      this.cliTypeDraft.set(nextCliType);
      this.loadModelCatalog(nextCliType);
    }

    if (isJobSwitch) {
      // Reset job-scoped UI state only when switching to a different job —
      // refreshes for the same job (e.g. execution status changes) must
      // preserve the live CLI output and view state.
      this.showLogOverlay.set(false);
      // Live/fresh work starts on Activity. Review/escalation starts on Result
      // even without status.md so verdict-less CLI activity can be summarized;
      // an existing summary keeps Result primary in every settled lane.
      const opensOnResult = d.info.state !== TaskState.Progress &&
        (!!d.statusMarkdown || d.info.state === TaskState.HumanReview || d.info.state === TaskState.Escalated);
      const routeInspectorTab = this.routeInspectorTab();
      this.activeInspectorTab.set(routeInspectorTab ?? (opensOnResult ? 'protocol' : 'activity'));
      this.userTouchedInspectorTab = routeInspectorTab !== null;
      this.showCliConfig.set(false);
      this.cliTestResult.set(null);
      this.editingPrompt.set(false);
      this.editingTitle.set(false);
      this.savingTitle.set(false);
      this.followupPrompt.set('');
      this.queuedFollowUp.set(false);
      this.chatError.set(null);
      this.setupExpandedDuringRun.set(false);
      this.cliPoll.resetForJobSwitch();
      this.lastShownFailureKey = null;
    }

    // Auto-promote Activity → Protocol the moment a fresh summary lands,
    // but only when the user hasn't actively chosen a tab themselves and the
    // job has left 3-progress — while the run is live we keep showing the
    // activity log even if a stale summary from a previous attempt exists.
    if (
      !this.userTouchedInspectorTab &&
      this.activeInspectorTab() === 'activity' &&
      d.info.state !== TaskState.Progress &&
      d.summaryState?.status === 'ready' &&
      d.statusMarkdown
    ) {
      this.activeInspectorTab.set('protocol');
    }

    // Symmetric counterpart: when a job we're watching transitions into
    // 3-progress (runner auto-pickup, manual start, or continuation from
    // review), demote Protocol → Activity so the live CLI output is what
    // the user sees instead of a stale summary from the previous run. Only
    // applies when the user hasn't manually picked the protocol tab.
    if (
      !this.userTouchedInspectorTab &&
      this.activeInspectorTab() === 'protocol' &&
      d.info.state === TaskState.Progress
    ) {
      this.activeInspectorTab.set('activity');
    }

    // Manual-regenerate poll lifecycle: once the backend flips out of
    // "generating", stop hammering the detail endpoint.
    if (this.regeneratingSummary() && d.summaryState?.status !== 'generating') {
      // Honour the small grace window (the very first request response usually
      // lands before the in-process state has flipped to "generating").
      if (Date.now() - this.regenStartedAt > 1500) {
        this.stopRegenPolling();
      }
    }

    this.cliPoll.setJob({ id: d.info.id, watchPath: d.info.watchPath });
    this.applyExecutionState(d.info.execution);
    if (d.info.execution?.status === 'running') {
      this.queuedFollowUp.set(false);
    }

    // The endpoint returns the live buffer while a process is active and falls
    // back to logs/cli-output.log for completed tasks.
    if (d.info.execution?.status === 'running' && !this.cliPoll.isPolling()) {
      this.cliPoll.startPolling();
    }
    this.jobService.getJobOutput(d.info.id, d.info.watchPath).subscribe({
      next: (output) => this.cliPoll.hydrateOutput(output, d.info.execution?.startedAt ?? null),
      error: (err) => {
        if (err.status !== 0) return; // silent for 404 etc
        this.showError(err);
      },
    });
  });

  private routeInspectorEffect = effect(() => {
    const routeTab = this.routeInspectorTab();
    void this.detail().info.taskKey;
    if (!routeTab) return;
    // An explicit deep-link choice is user intent even when it already
    // matches the initial tab. Mark it before the progress-state default can
    // demote Protocol to Activity and make the two effects fight each other.
    this.userTouchedInspectorTab = true;
    if (this.activeInspectorTab() !== routeTab) {
      this.activeInspectorTab.set(routeTab);
    }
  });
  private cliConfigEffect = effect(() => {
    const requestId = this.errorDialog.cliConfigRequest();
    if (requestId === 0 || requestId === this.lastCliConfigRequest) {
      return;
    }

    this.lastCliConfigRequest = requestId;
    this.openCliConfig();
  });
  private gitAutoRefreshEffect = effect(() => {
    // Only the active task's detail view shows the working tree; polling
    // git status on a non-active task would just churn for nothing and
    // would also pull working-tree state for a task that, by the
    // worktree-isolation rule, doesn't get to render that data.
    if (this.panesVisible().git && this.isActiveJob()) {
      this.git.startAutoRefresh();
    } else {
      this.git.stopAutoRefresh();
    }
  });

  ngOnDestroy() {
    this.detailEffect.destroy();
    this.cliConfigEffect.destroy();
    this.gitAutoRefreshEffect.destroy();
    this.cliPoll.stop();
    this.layout.stopLayoutResize();
    this.claudePoll.stop();
    this.stopRegenPolling();
  }

  /**
   * Re-run the Haiku summary for the current job. The backend writes status.md
   * and flips summaryState through generating → ready/failed; we poll detail
   * every 2 s (via fileSaved → parent re-fetch) so the UI follows the
   * transition. The detailEffect stops the timer once the status leaves
   * "generating".
   */
  regenerateProtocol(): void {
    if (this.regeneratingSummary()) return;
    const { id, watchPath } = this.detail().info;
    this.regeneratingSummary.set(true);
    this.regenStartedAt = Date.now();
    this.jobService.regenerateSummary(id, watchPath).subscribe({
      next: () => {
        // Immediate refresh — status flips to "generating" so the spinner shows.
        this.fileSaved.emit();
        this.startRegenPolling();
      },
      error: (err) => {
        this.stopRegenPolling();
        this.showError(err);
      },
    });
  }

  private startRegenPolling(): void {
    this.stopRegenPolling(false);
    this.regenPollTimer = setInterval(() => {
      // Hard cap: Haiku itself times out at 90 s; give a bit of slack for
      // process spawn + status file flush.
      if (Date.now() - this.regenStartedAt > 120_000) {
        this.stopRegenPolling();
        return;
      }
      this.fileSaved.emit();
    }, 2000);
  }

  private stopRegenPolling(clearFlag = true): void {
    if (this.regenPollTimer != null) {
      clearInterval(this.regenPollTimer);
      this.regenPollTimer = null;
    }
    if (clearFlag) this.regeneratingSummary.set(false);
  }

  // Bridge detail() changes to the ClaudeSessionPollService. The service
  // ignores no-op syncs and re-arms its 5 s timer only when the polled
  // job actually changes.
  private readonly claudeSessionEffect = effect(() => {
    this.claudePoll.syncTo(this.detail()?.info ?? null);
  });

  // Same bridge for the session-event poller (10 s cadence).
  private readonly sessionEventsEffect = effect(() => {
    this.sessionEventsPoll.syncTo(this.detail()?.info ?? null);
  });

  // ...and for the run-timeline poller (5 s cadence).
  private readonly runTimelineEffect = effect(() => {
    this.runTimelinePoll.syncTo(this.detail()?.info ?? null);
  });

  // ...and for the per-task event-ledger poller (10 s cadence, ADR-0049 /
  // ASS-566). Drives the Overview attempt-cycle indicator + the Timeline
  // tab; the panes inject the service directly for its signals.
  private readonly taskTimelinePoll = inject(TaskTimelinePollService);
  private readonly taskTimelineEffect = effect(() => {
    this.taskTimelinePoll.syncTo(this.detail()?.info ?? null);
  });

  // ...and for the agent-work-summary poller (10 s cadence). Drives
  // the Overview tab's Agent Work block.
  private readonly agentWorkSummaryPoll = inject(AgentWorkSummaryPollService);
  private readonly agentWorkSummaryEffect = effect(() => {
    this.agentWorkSummaryPoll.syncTo(this.detail()?.info ?? null);
  });

  // ...and for the per-job plan poller (5 s cadence). Drives the plan
  // strip above the activity log; the protocol pane injects the service
  // directly for its signal.
  private readonly planPoll = inject(PlanPollService);
  private readonly planEffect = effect(() => {
    this.planPoll.syncTo(this.detail()?.info ?? null);
  });

  private readonly taskPipelinePoll = inject(TaskPipelinePollService);
  private readonly taskPipelineEffect = effect(() => {
    this.taskPipelinePoll.syncTo(this.detail()?.info ?? null);
  });
  readonly statusIsSuperseded = computed<boolean>(() => {
    const generation = this.detail().statusGeneration;
    const execution = this.taskPipelinePoll.pipeline()?.execution;
    const currentAttempt = execution?.attempt;
    if (currentAttempt == null) return false;
    if (generation?.runIndex != null) return generation.runIndex < currentAttempt;
    return currentAttempt > 1
      && execution?.completedAt == null
      && !!this.detail().statusMarkdown?.trim();
  });
  readonly runOutcomePresentation = computed(() => deriveProtocolVerdict({
    isRunning: this.effectiveRunActive(),
    summaryStatus: this.detail().summaryState?.status ?? 'none',
    statusMarkdown: this.detail().statusMarkdown,
    outcomeIssue: this.detail().info.outcomeIssue,
    hasActivity: this.cliOutput().length > 0,
    laneState: this.detail().info.state,
    orchestratorVerdict: this.detail().info.orchestratorVerdict,
    statusSuperseded: this.statusIsSuperseded(),
    execution: this.detail().info.execution,
    pipelineExecution: this.taskPipelinePoll.pipeline()?.execution ?? null,
    activityOutcome: classifyLatestActivityOutcome(this.cliOutput()),
  }));

  private readonly screenshotsPoll = inject(ScreenshotsPollService);
  readonly screenshots = this.screenshotsPoll.screenshots;
  private readonly screenshotsEffect = effect(() => {
    this.screenshotsPoll.syncTo(this.detail()?.info ?? null);
  });

  private readonly artifactsService = inject(TaskArtifactsService);
  readonly artifacts = this.artifactsService.artifacts;
  private readonly artifactsEffect = effect(() => {
    this.artifactsService.syncTo(this.detail()?.info ?? null);
  });

  canStartJob(): boolean {
    const state = this.detail().info.state;
    return (state === TaskState.Ready || state === TaskState.Progress) && !this.isRunning();
  }

  /**
   * When the Start button is shown but should not actually fire, return a
   * short reason for the tooltip and disabled state. Today's case: another
   * job in the same project is already running on a CLI - the backend
   * rejects starts in this state, so disabling the button before the click
   * keeps the user from chasing a 4xx round-trip and explains why.
   */
  startDisabledReason = computed<string | null>(() => {
    const info = this.detail()?.info;
    if (!info) return null;
    const status = this.jobService.runnerStatus();
    const project = status?.projects?.[info.projectName];
    if (!project) return null;
    const activeId = project.activeJobId;
    if (!activeId || activeId === info.id) return null;
    return `Project "${info.projectName}" is already running ${activeId}. Stop the active run first or wait for it to finish.`;
  });

  /**
   * Whether the displayed task is the runner's currently-active job for
   * its project. Drives the worktree-isolation rule: working-tree info
   * (live `git status`, the "Accepted task work uncommitted" hygiene
   * warning) is only shown on the active task. Non-active tasks see only
   * their committed evidence; their detail view never speaks for changes
   * that belong to whichever task the agent is currently editing.
   */
  readonly isActiveJob = computed<boolean>(() => {
    const info = this.detail()?.info;
    if (!info) return false;
    const status = this.jobService.runnerStatus();
    const project = status?.projects?.[info.projectName];
    return project?.activeJobId === info.id;
  });

  readonly showQueuedFollowUp = computed<boolean>(() => {
    if (this.isRunning()) return false;
    return this.queuedFollowUp() || !!this.detail().info.pendingIntent;
  });

  /**
   * A saved follow-up sits on the card and no run has consumed it yet. Shown as
   * one quiet line so an operator can tell "the steer is waiting" from "the
   * steer is gone" without opening the job folder (AGT-2747).
   */
  readonly showPendingFollowUp = computed<boolean>(() => {
    if (this.isRunning()) return false;
    return !!this.detail().info.pendingIntent;
  });

  startJob(): void {
    this.errorMsg.set(null);
    this.starting.set(true);
    const model = this.modelDraft().trim() || undefined;
    const thinkingLevel = this.thinkingLevelDraft() ?? undefined;
    this.jobService.startJob(this.detail().info.id, this.detail().info.watchPath, model, undefined, thinkingLevel).subscribe({
      next: (resp) => {
        this.starting.set(false);
        if (resp.status === 'started' && resp.execution) {
          this.cliPoll.beginRun(new Date(resp.execution.startedAt));
          this.sessionEventsPoll.refresh();
        }
        // status === 'queued': no modal, no error. The orchestrator's
        // [queued] meta line lands in the activity log on the next poll
        // tick. The job's pendingIntent badge will appear on the card.
      },
      error: (err) => {
        this.starting.set(false);
        this.showError(err);
      },
    });
  }

  stopJob(): void {
    this.errorMsg.set(null);
    this.jobService.stopJob(this.detail().info.id, this.detail().info.watchPath).subscribe({
      next: () => this.cliPoll.stop(),
      error: (err) => this.showError(err),
    });
  }

  continueJob(): void {
    const prompt = this.followupPrompt().trim();
    if (!prompt) return;

    this.errorMsg.set(null);
    this.chatError.set(null);
    this.continuing.set(true);
    // Echo the user's message into the activity log immediately so the chat
    // feels responsive — the backend writes the same line to cli-output.log
    // on success, and the next poll dedupes the optimistic copy.
    this.cliPoll.appendOptimisticUserMessage(prompt);
    this.followupPrompt.set('');
    this.jobService
      .continueJob(
        this.detail().info.id,
        prompt,
        this.detail().info.watchPath,
        undefined,
        undefined,
        undefined,
        'continue',
      )
      .subscribe({
        next: (resp) => {
          this.continuing.set(false);
          if (resp.status === 'started' && resp.execution) {
            this.queuedFollowUp.set(false);
            this.cliPoll.beginContinuation(new Date(resp.execution.startedAt));
            this.sessionEventsPoll.refresh();
          } else if (resp.status === 'queued') {
            this.queuedFollowUp.set(true);
          }
          // status === 'queued': the project was busy. The backend already
          // saved the user's intent + posted a [queued] orchestrator line
          // into the chat. The optimistic user-message echo above and the
          // queued status band keep the conversation readable until pickup.
        },
        error: (err) => {
          this.continuing.set(false);
          // Restore the user's text so they can correct or retry — the backend
          // never accepted it, so the optimistic echo above is a lie we shouldn't
          // leave on screen permanently. We keep it visible for now (so the user
          // can see what they tried) but the inline error banner explains why.
          this.followupPrompt.set(prompt);
          this.chatError.set(this.showError(err));
        },
      });
  }

  canSendChat(): boolean {
    if (this.continuing()) return false;
    if (!this.followupPrompt().trim()) return false;
    return true;
  }

  chatSendLabel(): string {
    return this.continuing() ? 'Sending…' : 'Send';
  }

  sendChatMessage(): void {
    const prompt = this.followupPrompt().trim();
    if (!prompt || this.continuing()) return;

    if (!this.isRunning()) {
      this.continueJob();
      return;
    }

    // Pause-and-send: stop the running CLI first, then continue with the
    // user's intervention as a follow-up prompt. Reason 'followup' tells
    // the backend to mark the resulting CliExecution as 'stopped' (not
    // 'failed', exitCode -1) so applyExecutionState below does not pop a
    // crash modal between the kill and the follow-up start.
    this.errorMsg.set(null);
    this.chatError.set(null);
    this.continuing.set(true);
    this.jobService
      .stopJob(this.detail().info.id, this.detail().info.watchPath, 'followup')
      .subscribe({
        next: () => {
          this.isRunning.set(false);
          this.continuing.set(false);
          this.continueJob();
        },
        error: (err) => {
          this.continuing.set(false);
          this.chatError.set(this.showError(err));
        },
      });
  }

  onModelDraftChange(value: string): void {
    const trimmed = (value ?? '').trim();
    this.modelDraft.set(trimmed);
    const current = this.detail().info.model ?? '';
    if (trimmed === current) return;

    this.jobService
      .setJobModel(
        this.detail().info.id,
        trimmed === '' ? null : trimmed,
        this.detail().info.watchPath,
      )
      .subscribe({
        error: (err) => this.showError(err),
      });
  }

  onThinkingLevelDraftChange(value: string | null): void {
    this.thinkingLevelDraft.set(value);
    const current = this.detail().info.thinkingLevel ?? null;
    if (value === current) return;

    this.jobService
      .setJobThinkingLevel(
        this.detail().info.id,
        value,
        this.detail().info.watchPath,
      )
      .subscribe({
        error: (err) => this.showError(err),
      });
  }

  /**
   * Atomic CLI + model commit from the unified selector. The picker
   * collects both fields locally; this handler sequences the two PUT calls
   * (cli-type, then model) and updates the local draft signals optimistically.
   * `model` of '' clears back to the CLI default. The two backend endpoints
   * are independent, but ordering matters: cli-type first ensures the
   * subsequent model PUT validates against the new CLI's catalog.
   *
   * ADR-0046 (Optimistic-UI). The badge re-renders from `cliTypeDraft` /
   * `modelDraft` synchronously here, before either PUT reaches the wire.
   * On a non-network error the previous values are restored and the user
   * gets a clear toast via `showError`; on a network failure the parent's
   * refresh-on-fileSaved path heals the UI back to the on-disk state.
   */
  onAgentConfigCommit(change: { cliType: CliType; model: string; thinkingLevel: string | null }): void {
    const info = this.detail().info;
    const previousCli = this.cliTypeDraft();
    const previousModel = this.modelDraft();
    const previousThinkingLevel = this.thinkingLevelDraft();
    const previousCatalog = this.availableModels();
    const currentCli = previousCli;
    const currentModel = (info.model ?? '').trim();
    const currentThinkingLevel = info.thinkingLevel ?? null;
    const cliChanged = change.cliType !== currentCli;
    const modelChanged = change.model !== currentModel;
    const thinkingChanged = change.thinkingLevel !== currentThinkingLevel;
    if (!cliChanged && !modelChanged && !thinkingChanged) return;

    // Optimistic local update so the badge re-renders instantly without
    // waiting for the parent's detail re-fetch.
    if (cliChanged) {
      this.cliTypeDraft.set(change.cliType);
      // Refresh the visible catalog to match the new CLI so the legacy
      // commandbar dropdown does not show stale options between PUT and
      // detail-refetch. Reads through CliCatalogStore (ADR-0046) so the
      // cache hit is synchronous when the catalog was already hydrated.
      this.loadModelCatalog(change.cliType);
    }
    this.modelDraft.set(change.model);
    this.thinkingLevelDraft.set(change.thinkingLevel);

    const revert = () => {
      this.cliTypeDraft.set(previousCli);
      this.modelDraft.set(previousModel);
      this.thinkingLevelDraft.set(previousThinkingLevel);
      this.availableModels.set(previousCatalog);
    };

    const modelPut = () => {
      this.jobService
        .setJobModel(
          info.id,
          change.model === '' ? null : change.model,
          info.watchPath,
        )
        .subscribe({
          next: () => {
            if (thinkingChanged) thinkingPut();
            else this.fileSaved.emit();
          },
          error: (err) => {
            revert();
            this.showError(err);
          },
        });
    };

    const thinkingPut = () => {
      this.jobService
        .setJobThinkingLevel(info.id, change.thinkingLevel, info.watchPath)
        .subscribe({
          next: () => this.fileSaved.emit(),
          error: (err) => {
            revert();
            this.showError(err);
          },
        });
    };

    if (cliChanged) {
      this.jobService
        .setJobCliType(info.id, change.cliType, info.watchPath)
        .subscribe({
          next: () => {
            if (modelChanged) modelPut();
            else if (thinkingChanged) thinkingPut();
            else this.fileSaved.emit();
          },
          error: (err) => {
            revert();
            this.showError(err);
          },
        });
      return;
    }

    if (modelChanged) modelPut();
    else if (thinkingChanged) thinkingPut();
  }

  private showError(err: unknown): string {
    const detail = err as { status?: number; statusText?: string; message?: string; error?: unknown };
    const bodyError =
      typeof detail.error === 'object' && detail.error !== null && 'error' in detail.error
        ? (detail.error as { error?: unknown }).error
        : detail.error;
    const message =
      detail.status === 0
        ? 'Backend not reachable — is the API running on localhost:5030?'
        : (typeof bodyError === 'string' && bodyError.trim()) ||
          `Request failed (${detail.status || 'unknown'}): ${detail.statusText || detail.message || 'Unknown error'}`;

    this.errorMsg.set(message);
    this.errorDialog.show(err, {
      title: 'Task action failed',
      fallbackMessage: message,
      source: `Task ${this.detail().info.id}`,
      canOpenCliConfig: this.canOpenCliConfigForCurrentJob(message),
    });
    return message;
  }

  private applyExecutionState(
    execution: import('../../models/task.model').CliExecution | null,
  ): void {
    if (!execution) return;
    this.cliPoll.applyExecution(execution);
    // 'stopped' is the deliberate-kill status (user pause, Pause-&-Send,
    // watchdog kill, host shutdown). It is not a crash and must NOT open
    // the failure modal; otherwise every Pause-&-Send produces a false
    // alarm. Clear any stale error banner left over from a real prior
    // failure on the same job so the UI does not look broken.
    if (execution.status === 'stopped') {
      this.errorMsg.set(null);
      return;
    }
    if (shouldShowFailureToast(execution)) {
      const message =
        execution.exitCode === null
          ? 'Task execution failed.'
          : `Task execution failed with exit code ${execution.exitCode}.`;
      this.errorMsg.set(message);

      // The backend keeps the failed CliExecution in memory until the next run,
      // so the same snapshot arrives on every detail refresh and on every job
      // re-open. Without de-duping we'd block the detail view behind a modal
      // every 2 s. Key by taskKey + startedAt so a fresh failure (different
      // startedAt) still surfaces.
      const failureKey = `${execution.taskKey}|${execution.startedAt}`;
      if (this.lastShownFailureKey === failureKey) return;
      this.lastShownFailureKey = failureKey;

      this.errorDialog.show(message, {
        title: 'Task execution failed',
        fallbackMessage: message,
        source: `Task ${this.detail().info.id}`,
        output: { execution, cliOutput: this.cliOutput() },
      });
    }
  }

  isProgress(): boolean {
    return this.detail().info.state === TaskState.Progress;
  }

  /**
   * Triage panel: route a typed lane action to the right handler. Move /
   * delete / move-to-top go to the parent (it owns the optimistic-paint and
   * auto-advance to the next peer); start / stop / editPrompt / showActivity
   * are local operations against the open job.
   */
  onTriageAction(payload: TriageActionPayload): void {
    if (this.mutationsBlocked() || this.triageActingId() !== null) return;
    const { id, intent } = payload;
    switch (intent.kind) {
      case 'move':
        this.triageActingId.set(id);
        this.triageMoveRequested.emit({ targetState: intent.targetState, actionId: id });
        return;
      case 'moveToTop':
        this.triageActingId.set(id);
        this.triageMoveToTopRequested.emit({ actionId: id });
        return;
      case 'delete':
        this.triageActingId.set(id);
        this.triageDeleteRequested.emit({ actionId: id });
        return;
      case 'start':
        this.triageActingId.set(id);
        this.triageStartRequested.emit({ actionId: id });
        return;
      case 'stop':
        this.triageActingId.set(id);
        this.stopJob();
        // Stop is local-only; clear after the request kicks off.
        queueMicrotask(() => this.triageActingId.set(null));
        return;
      case 'editPrompt':
        if (!this.panesVisible().prompt) this.togglePane('prompt');
        this.startEdit('prompt');
        return;
      case 'showActivity':
        this.activeInspectorTab.set('activity');
        this.userTouchedInspectorTab = true;
        return;
    }
  }

  /** Resets the per-button spinner after a triage action settles. */
  clearTriageActing(): void {
    this.triageActingId.set(null);
  }

  /** Keeps j/k global but gives arrow paging only to task navigation focus.
   * Escape remains owned by ModalStackService. */
  @HostListener('document:keydown', ['$event'])
  onTriageKey(event: KeyboardEvent): void {
    if (!taskDetailShortcutTargetAllowed(event)) return;
    const boardOwnsFocus = taskNavigationOwnsFocus(event);
    if (this.editingTitle() || this.editingPrompt()) return;
    if (this.showLogOverlay() || this.showCliConfig()) return;

    switch (event.key) {
      case 'j':
      case 'ArrowDown':
      case 'ArrowRight':
        if (!boardOwnsFocus && event.key.startsWith('Arrow')) return;
        event.preventDefault();
        this.nextInLaneRequested.emit();
        return;
      case 'k':
      case 'ArrowUp':
      case 'ArrowLeft':
        if (!boardOwnsFocus && event.key.startsWith('Arrow')) return;
        event.preventDefault();
        this.prevInLaneRequested.emit();
        return;
      case 'Enter':
        if (this.mutationsBlocked() || this.triageActingId() !== null) return;
        this.detailHeaderRef?.triggerPrimary();
        event.preventDefault();
        return;
    }
  }

  /** Re-points the pager without moving the open task. */
  onNavigateLane(lane: string) {
    this.navigateLaneRequested.emit(lane);
  }

  /** Moves this Ready task to the queue head through the atomic backend endpoint. */
  moveToTopOfReady(): void {
    if (this.movingToTop()) return;
    const info = this.detail().info;
    if (info.state !== TaskState.Ready) return;

    // Capture the lane order so Undo can replay it through /api/tasks/reorder.
    const prevOrder = this.captureLaneOrder(TaskState.Ready);
    this.movingToTop.set(true);
    this.jobService.beginOptimisticPersist();
    this.jobService.moveJobToTop(info.id, info.watchPath).subscribe({
      next: () => {
        this.jobService.endOptimisticPersist();
        this.movingToTop.set(false);
        if (prevOrder.length > 0) {
          this.undo.offerReorderRevert({
            jobId: info.id,
            jobLabel: info.title || info.id,
            actionLabel: 'Moved',
            targetLaneLabel: `top of ${laneLabelFor(TaskState.Ready)}`,
            laneState: TaskState.Ready,
            prevOrder,
          });
        }
      },
      error: (err) => {
        this.jobService.endOptimisticPersist();
        this.movingToTop.set(false);
        this.errorDialog.show(err, {
          title: 'Failed to move task to top',
          fallbackMessage: 'Failed to move task to the top of the Ready queue',
          source: `Task ${info.id}`,
        });
      },
    });
  }

  startEdit(which: 'prompt') {
    if (this.isRunning()) return;
    if (which === 'prompt') {
      this.promptDraftValue = this.detail().promptMarkdown ?? '';
      this.editingPrompt.set(true);
    }
  }

  /** Captures a lane order for replay through the reorder endpoint. */
  private captureLaneOrder(state: string): { jobId: string; watchPath: string }[] {
    const grouped = this.jobService.grouped();
    const out: { jobId: string; watchPath: string }[] = [];
    for (const list of boardLanes(grouped)) {
      for (const j of list) {
        if (j.state === state) out.push({ jobId: j.id, watchPath: j.watchPath });
      }
    }
    return out;
  }

  /** Prevents the summary-ready auto-switch from overriding a manual tab choice. */
  onInspectorTabChange(tab: 'task' | 'activity' | 'protocol') {
    this.userTouchedInspectorTab = true;
    this.activeInspectorTab.set(tab);
    this.inspectorTabChange.emit(tab);
  }

  /** Toggles the running setup bar between compact and full selectors. */
  toggleSetupCollapsed() {
    this.setupExpandedDuringRun.update((v) => !v);
  }

  startTitleEdit() {
    if (this.editingTitle()) return;
    this.titleDraft.set(this.detail().info.title || this.detail().info.id);
    this.editingTitle.set(true);
  }

  cancelTitleEdit() {
    this.editingTitle.set(false);
    this.savingTitle.set(false);
  }

  /** Inline title save from a child pane (e.g. the epic card) by value. */
  saveTitleValue(title: string) {
    this.titleDraft.set(title);
    this.saveTitle();
  }

  saveTitle() {
    const trimmed = this.titleDraft().trim();
    if (!trimmed || this.savingTitle()) return;
    const current = this.detail().info.title || this.detail().info.id;
    if (trimmed === current) {
      this.editingTitle.set(false);
      return;
    }

    this.savingTitle.set(true);
    this.jobService
      .setJobTitle(this.detail().info.id, trimmed, this.detail().info.watchPath)
      .subscribe({
        next: () => {
          this.savingTitle.set(false);
          this.editingTitle.set(false);
          this.fileSaved.emit();
        },
        error: (err) => {
          this.savingTitle.set(false);
          this.showError(err);
        },
      });
  }

  cancelEdit(which: 'prompt') {
    if (which === 'prompt') this.editingPrompt.set(false);
  }

  saveFile(fileName: string) {
    if (this.isRunning()) return;
    if (fileName !== 'prompt.md') return;
    this.saveFileContent(fileName, this.promptDraftValue);
  }

  saveFileContent(fileName: string, content: string) {
    if (this.isRunning()) return;
    this.jobService
      .updateJobFile(this.detail().info.id, fileName, content, this.detail().info.watchPath)
      .subscribe({
        next: () => {
          if (fileName === 'prompt.md') this.editingPrompt.set(false);
          this.artifactsService.refresh();
          this.fileSaved.emit();
        },
        error: (err) => this.showError(err),
      });
  }

  handleFileKeydown(event: KeyboardEvent, fileName: string): void {
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 's') {
      event.preventDefault();
      this.saveFile(fileName);
    }
  }

  // LayoutPanesService facades.

  startLayoutResize(event: PointerEvent): void {
    this.layout.startLayoutResize(event);
  }

  togglePane(name: 'prompt' | 'protocol' | 'git'): void {
    const next = this.layout.togglePane(name);
    if (name === 'git' && next.git && !this.gitStatus()) {
      // Lazy-load git status the first time the pane is shown.
      this.refreshGit();
    }
  }

  toggleMaximize(name: 'prompt' | 'protocol' | 'git'): void {
    this.layout.toggleMaximize(name);
  }

  isPaneRendered(name: 'prompt' | 'protocol' | 'git'): boolean {
    return this.layout.isPaneRendered(name);
  }

  firstVisibleAfter(name: 'prompt' | 'protocol' | 'git'): 'protocol' | 'git' {
    return this.layout.firstVisibleAfter(name);
  }

  startPaneResize(
    event: PointerEvent,
    left: 'prompt' | 'protocol',
    right: 'protocol' | 'git',
  ): void {
    this.layout.startPaneResize(event, left, right);
  }

  // GitPaneService facades retained for existing same-class call sites.

  refreshGit(): void {
    this.git.refresh();
  }
  openInVsCode(): void {
    this.git.openInVsCode();
  }
  generateCommitMessage(): void {
    if (this.isRunning() || !this.commitActionsAvailable()) return;
    this.git.generateCommitMessage();
  }
  addCommitFromMenu(): void {
    if (this.isRunning() || !this.commitActionsAvailable() || this.committing()) return;
    const initial = this.git.commitMessage();
    const message = window.prompt('Commit message', initial);
    if (message === null) return;
    const trimmed = message.trim();
    if (!trimmed) return;
    this.git.commitMessage.set(trimmed);
    this.git.commit();
  }

  // ClaudeSessionPollService formatting facades.

  formatTokens(n: number): string {
    return formatTokens(n);
  }

  claudeSessionTooltip(): string {
    return claudeSessionTooltip(this.claudeSession());
  }

  formatRateWindow(window: string | null): string {
    return formatRateWindow(window);
  }

  formatResetIn(epochSeconds: number): string {
    return formatResetIn(epochSeconds, this.nowTick());
  }

  rateLimitTooltip(): string {
    return rateLimitTooltip(this.claudeRateLimit());
  }

  stateLabel(state: string): string {
    return stateLabel(state);
  }

  formatTime(dateStr: string): string {
    return formatTime(dateStr);
  }

  formatDate(dateStr: string): string {
    return formatDate(dateStr);
  }

  formatDateTime(dateStr: string): string {
    return formatDateTime(dateStr);
  }

  isCliError(): boolean {
    const msg = this.errorMsg();
    return isCliErrorMessage(msg);
  }

  openCliConfig(): void {
    // Copilot removed: no CLI exposes the inline path/token config card.
    return;
    this.showCliConfig.set(true);
    this.cliTestResult.set(null);
    this.jobService.getCliSettings().subscribe({
      next: (settings) => {
        this.cliStatus.set(settings);
        this.cliPathDraft.set(settings.path);
      },
      error: (err) => this.showError(err),
    });
  }

  dismissError(): void {
    this.errorMsg.set(null);
    this.showCliConfig.set(false);
  }

  testCliPath(): void {
    const path = this.cliPathDraft().trim();
    if (!path) return;
    this.cliTesting.set(true);
    this.cliTestResult.set(null);
    this.jobService.testCliPath(path).subscribe({
      next: (result) => {
        this.cliTestResult.set(result);
        this.cliTesting.set(false);
      },
      error: (err) => {
        this.cliTesting.set(false);
        this.showError(err);
      },
    });
  }

  saveCliPath(): void {
    const path = this.cliPathDraft().trim();
    if (!path) return;
    this.cliTesting.set(true);
    this.jobService.setCliPath(path).subscribe({
      next: (result) => {
        this.cliStatus.set(result);
        this.cliTestResult.set(null);
        this.cliTesting.set(false);
        if (result.available) {
          this.errorMsg.set(null);
          this.showCliConfig.set(false);
        }
      },
      error: (err) => {
        this.cliTesting.set(false);
        this.showError(err);
      },
    });
  }

  saveToken(): void {
    const token = this.tokenDraft().trim();
    if (!token) return;
    this.tokenSaving.set(true);
    this.jobService.setGitHubToken(token).subscribe({
      next: (result) => {
        this.cliStatus.set(result);
        this.tokenSaving.set(false);
        this.tokenDraft.set('');
        if (result.hasToken && result.available) {
          this.errorMsg.set(null);
        }
      },
      error: (err) => {
        this.tokenSaving.set(false);
        this.showError(err);
      },
    });
  }

  onProjectChange(targetWatchPath: string) {
    if (targetWatchPath === this.detail().info.watchPath) return;
    this.jobService
      .changeProject(this.detail().info.id, targetWatchPath, this.detail().info.watchPath)
      .subscribe({
        next: () => this.projectChanged.emit(targetWatchPath),
        error: (err) => this.showError(err),
      });
  }

  private canOpenCliConfigForCurrentJob(message: string | null | undefined): boolean {
    // Copilot removed: no CLI exposes the inline config card.
    void message;
    return false;
  }
}
