import { ChangeDetectionStrategy, Component, ElementRef, OnDestroy, OnInit, computed, effect, inject, input, output, signal } from '@angular/core';
import type { AutoLoopSnapshot, TaskExecutionLocation, TaskInfo, PendingIntent, EpicRollup } from '../../../../models/task.model';
import { TaskState } from '../../../../models/task.model';
import { GitSummaryService } from '../../../../services/git-summary.service';
import { TaskService } from '../../../../services/task.service';
import { ClientService } from '../../../../services/client.service';
import { CodeReviewActivityStore } from '../../../../services/code-review-activity.store';
import { cliTypeIcon, stateLabel } from '../../../../services/format.util';
import { projectIdentity } from '../../../../services/project-identity.util';
import { TagRegistryStore } from '../../../../services/tag-registry.store';
import {
  buildCardCtxMenuItems,
  buildCodeReviewGradeBadge,
  buildVisibleDependencyChip,
  buildCommitChainTooltip,
  buildCommitChainView,
  buildCommitEmptyBadge,
  buildCooldownRetryBanner,
  currentIntegrationStatus,
  buildEffectiveModelChip,
  buildExecutionBadge,
  buildGitStateBadge,
  buildExternalDoneBadge,
  buildLoopTooltip,
  buildMergeSignal,
  buildModeBadge,
  buildOutcomeIssueBadge,
  buildOwnerChip,
  buildPendingTooltip,
  buildPhaseBadge,
  buildPipelineDots,
  buildReviewBadge,
  buildTagChips,
  buildTaskTypeChip,
  buildTokenBubble,
  resolveDependencyTarget,
  cardNeedsAttention,
  commitChainVariant,
  formatTokens,
  EPIC_ASSIGN_PREFIX,
  EPIC_DETACH_ID,
  FILTER_DEPENDENTS_ID,
  DELETE_ID,
  type CommitChainView,
  type CommitEmptyBadge,
  type DependencyChip,
} from './task-card-view-model';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { TaskStatusPopoverDirective } from '../../../../components/task-status-card';
import { MenuComponent, MenuItemClickEvent } from '../../../../components/menu';
import { StudioIconComponent, type StudioIconName } from '../../../../components/studio-icon/studio-icon.component';
import { ModelLevelIndicatorComponent } from '../../../../components/model-level-indicator/model-level-indicator.component';
import { ExecutionLocationBadgeComponent } from '../../../../components/execution-location-badge/execution-location-badge.component';
import { IntegrationStatusBadgeComponent } from '../../../../components/integration-status-badge/integration-status-badge.component';
import { ReviewDecisionBadgesComponent } from '../review-decision-badges/review-decision-badges.component';
import { TaskLiveStatusComponent } from '../../../../components/task-live-status/task-live-status.component';
import { TokenPopoverDirective } from './token-popover.directive';
import { TaskTokenUsagePopoverComponent } from './token-usage-popover/token-usage-popover.component';
import { TaskCardQuotaWaitComponent } from '../task-card-quota-wait/task-card-quota-wait.component';
import { taskCardNow } from './task-card-clock';
import { NotificationService } from '../../../../services/notification.service';
import { copyTextToClipboard } from '../../../../services/clipboard.util';
import { deriveStalledTaskState, isTaskRunActive } from '../../../../services/run-activity.util';
import { BoardFiltersService } from '../../state/board-filters.service';
import { EpicExpansionStore } from '../../state/epic-expansion.service';
import { TaskSelectionService } from '../../../task-detail';
import { PostProcessingActivityComponent } from '../post-processing-activity/post-processing-activity.component';
import { TestEvidenceStatusComponent } from '../../../test-evidence';
import { CopyableTaskKeyComponent } from '../../../../components/copyable-task-key/copyable-task-key.component';
import { ProviderAuthStatusService, providerAuthWaitReason } from '../../../remote-hosts';
// Shared 'now' signal that ticks every 30s so all relative timestamps update in lockstep
// without re-reading Date.now() during change detection (which causes NG0100).
const nowTick = signal(Date.now());
if (typeof window !== 'undefined') {
  setInterval(() => nowTick.set(Date.now()), 30_000);
}

@Component({
  selector: 'app-task-card, app-job-card',
  standalone: true,
  imports: [TooltipDirective, TaskStatusPopoverDirective, MenuComponent, StudioIconComponent, TokenPopoverDirective, TaskTokenUsagePopoverComponent, ModelLevelIndicatorComponent, ExecutionLocationBadgeComponent, IntegrationStatusBadgeComponent, ReviewDecisionBadgesComponent, PostProcessingActivityComponent, TestEvidenceStatusComponent, TaskLiveStatusComponent, TaskCardQuotaWaitComponent, CopyableTaskKeyComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './task-card.component.html',
  styleUrl: './task-card.component.scss',
})
export class TaskCardComponent implements OnInit, OnDestroy {
  readonly job = input.required<TaskInfo>();
  readonly epicSubTasks = input<readonly TaskInfo[]>([]);
  readonly compact = input<boolean>(false);
  readonly mutationsBlocked = input<boolean>(false);
  /**
   * F2: when set and matches this card's job id, the card renders the
   * "just created" pulse highlight and scrolls itself into view on the
   * board. The host clears the signal after one animation cycle.
   */
  readonly highlightJobId = input<string | null>(null);
  readonly deleteRequested = output<TaskInfo>();
  readonly subTaskClick = output<TaskInfo>();
  /**
   * F5: emitted when the user clicks the inline "Pick next" affordance
   * on a 2-ready card. The host wires this to `moveJobToTop` so the
   * runner picks it up on the next cycle.
   */
  readonly pickNextRequested = output<TaskInfo>();
  private readonly hostRef = inject(ElementRef<HTMLElement>);

  /** True when this card should render the just-created highlight. */
  readonly isJustCreated = computed(() => this.highlightJobId() === this.job().id);

  /** Scroll a newly created card into the board viewport. */
  private readonly scrollEffect = effect(() => {
    if (!this.isJustCreated()) return;
    queueMicrotask(() => {
      try {
        this.hostRef.nativeElement.scrollIntoView({ behavior: 'smooth', block: 'nearest', inline: 'nearest' });
      } catch { /* SSR / detached DOM — ignore */ }
    });
  });
  private readonly gitSummary = inject(GitSummaryService);
  private readonly clients = inject(ClientService);
  private readonly tagRegistry = inject(TagRegistryStore);
  private readonly codeReviewActivity = inject(CodeReviewActivityStore);
  private readonly providerAuthStatus = inject(ProviderAuthStatusService);
  private stopPolling: (() => void) | null = null;

  readonly taskTypeChip = computed(() => buildTaskTypeChip(this.job().taskType));

  taskTypeIconName(kind: string): StudioIconName {
    if (kind === 'bug') return 'warn';
    if (kind === 'feature') return 'plus';
    return 'dot';
  }

  readonly issueLabel = computed(() => this.job().key || this.job().id);

  /**
   * Mode badge (planning / research). Null for coding so the board stays quiet
   * for the default mode; planning and research cards become recognizable at a
   * glance. See {@link buildModeBadge}.
   */
  readonly modeBadge = computed(() => buildModeBadge(this.job().mode));

  readonly tagChips = computed(() => buildTagChips(this.job().tags, this.tagRegistry.byId(), this.job().state));

  readonly publishableChip = computed(() => {
    if (this.job().state !== TaskState.Completed) return null;
    const labels = this.job().publishSignal?.labels ?? [];
    if (labels.length === 0) return null;
    return { labels, text: labels.join(', ') };
  });

  /** True for cards where "Pick next" makes sense (front-of-queue promotion). */
  readonly canPickNext = computed(() => this.job().state === TaskState.Ready && !this.mutationsBlocked());

  onPickNextClick(event: Event) {
    event.stopPropagation();
    if (this.mutationsBlocked()) return;
    this.pickNextRequested.emit(this.job());
  }
  readonly ownerChip = computed(() => {
    const ownerId = this.job().ownerClientId;
    if (!ownerId) return null;
    return buildOwnerChip(this.clients.resolve(ownerId));
  });

  // Live working-tree state (branch + uncommitted file count) only makes
  // sense while the agent is actively touching the repo. In review lanes
  // the task is "frozen" against a specific commit and live state would
  // misrepresent it (the board's project status is shared across cards;
  // a card sitting in 5-human-review must not advertise the dev branch
  // someone else just switched to). Pre-work and post-review lanes carry
  // no useful per-task git context either, so the pill is suppressed
  // everywhere except 3-progress.
  private static readonly LANES_WITH_GIT = new Set<string>([
    TaskState.Progress,
  ]);

  readonly gitPill = computed(() => {
    if (!TaskCardComponent.LANES_WITH_GIT.has(this.job().state)) return null;
    const projectName = this.job().projectName;
    const summary = this.gitSummary.value().find(s => s.projectName === projectName);
    return summary && summary.isRepo ? summary : null;
  });

  /**
   * Commit-chain view model (AC#1/#4). Reads the attributed `commits[]`
   * chain (single source of truth), falling back to the legacy singular
   * `commit` only when `commits[]` is absent. Never sources commit data
   * from repo HEAD / the working tree - that was bug (1) ("main: 20 files"
   * leaking into review lanes).
   */
  readonly commitChainView = computed<CommitChainView | null>(() => {
    const variant = commitChainVariant(this.job().state);
    if (!variant) return null;
    return buildCommitChainView(this.job(), variant);
  });

  /**
   * Zero-commit diagnostic for review-lane cards (AC#3, bug (3)). See
   * {@link buildCommitEmptyBadge}.
   */
  readonly commitEmptyBadge = computed<CommitEmptyBadge | null>(() => buildCommitEmptyBadge(this.job()));

  readonly gitTooltip = computed(() => {
    const g = this.gitPill();
    if (!g) return '';
    return `Branch: ${g.branch ?? '(detached)'}\n${g.filesChanged} changed file(s) in ${g.rootPath}\n+${g.totalAdded} / −${g.totalRemoved}`;
  });

  /**
   * Commit-chain tooltip. A single commit lists the files it touched; a
   * multi-commit chain lists every SHA with its subject and rolled-up file
   * total so a card carrying auto-review concerns makes the affected scope
   * visible without opening the job. HTML escaping is handled by the tooltip
   * controller's DOMPurify pass.
   */
  readonly commitTooltip = computed(() => buildCommitChainTooltip(this.job()));

  ngOnInit(): void { this.stopPolling = this.gitSummary.ensurePolling(); }
  ngOnDestroy(): void { this.stopPolling?.(); }

  phaseBadge() {
    if (this.job().state === TaskState.AutoReview) return null;
    return buildPhaseBadge(this.job().phase, this.job().steerPendingSince ?? this.job().phaseEnteredAt, taskCardNow(), this.job().state);
  }

  executionBadge() { return buildExecutionBadge(this.job()); }

  readonly displayedExecutionLocation = computed<TaskExecutionLocation | null>(() => {
    const job = this.job();
    if (job.executionLocation) return job.executionLocation;
    const runner = job.state === TaskState.Progress ? job.runner : null;
    if (!runner?.isRemote) return null;
    return {
      state: 'remote-running',
      executionKind: 'remote',
      runnerId: runner.runnerName || runner.runnerId,
      hostDisplayName: runner.hostname,
      startedAt: runner.acquiredAt,
      lastHeartbeat: runner.acquiredAt,
      lastActivityAt: runner.acquiredAt,
      connectionState: 'connected',
      leaseState: 'active',
      trustReason: 'The task server holds the fenced run lease.',
    };
  });

  readonly reviewBadge = computed(() => buildReviewBadge(this.job().summaryState));

  /**
   * "extern erledigt" badge for a task completed out-of-band and reconciled via
   * the external-completion endpoint. Null on every task finished through the
   * normal runner/review path. See {@link buildExternalDoneBadge}.
   */
  readonly externalDoneBadge = computed(() => buildExternalDoneBadge(this.job()));

  /**
   * Quality-grade badge (ASS-1657). The automatic code-review step grades every
   * pipelined task A/B/C/D and hangs a `code-review:grade-{a-d}` tag; this lifts
   * that grade into a prominent colour-coded badge. Null until the grade step
   * has run. See {@link buildCodeReviewGradeBadge}.
   */
  readonly codeReviewGradeBadge = computed(() => buildCodeReviewGradeBadge(this.job().tags));

  /**
   * Git-integration-state badge (ASS-1665). Pure lane-derived: pre-merge (work
   * on the task branch) / post-merge (in develop) / tagged (archived). No
   * `state` key rename and no new backend field — see {@link buildGitStateBadge}.
   */
  readonly gitStateBadge = computed(() => buildGitStateBadge(this.job()));

  readonly pipelineDots = computed(() => buildPipelineDots(this.job()));

  readonly changeContext = computed(() => {
    const live = this.gitPill();
    const gitState = this.gitStateBadge();
    const commits = this.commitChainView();
    const empty = this.commitEmptyBadge();
    if (!live && !gitState && !commits && !empty) return null;

    const liveBranch = live?.branch ?? null;
    const liveMatchesGitState = !!live && (!gitState || gitState.label === 'main checkout' || liveBranch === gitState.label);
    const displayLive = liveMatchesGitState ? live : null;
    const displayGitState = displayLive ? null : gitState;
    const sharedCheckout = displayGitState?.label === 'main checkout';
    const kind = displayLive || sharedCheckout ? 'worktree' : displayGitState?.kind === 'tagged' ? 'archive' : 'branch';
    // `label` stays the compact semantic code ('WT' / 'BR' / 'TAG') consumed by
    // the change-context specs, but it is NO LONGER rendered on the card: the
    // operator could not decode "BR" (AGT-2046). The card now shows a branch /
    // archive icon plus the branch name and a plain-text tooltip instead.
    const label = displayLive || sharedCheckout ? 'WT' : displayGitState?.kind === 'tagged' ? 'TAG' : 'BR';
    const refIcon = kind === 'archive' ? '🏷' : '⎇';
    const value = displayLive?.branch || displayGitState?.label || '';
    const refTooltip = kind === 'archive'
      ? `Archived${value ? `: ${value}` : ''} — out of the active git flow.`
      : kind === 'worktree'
        ? `Working tree${value ? `: ${value}` : ''}`
        : `Branch${value ? `: ${value}` : ''}`;
    const summary = displayLive
      ? displayLive.filesChanged === 0 ? 'clean' : `${displayLive.filesChanged} ${displayLive.filesChanged === 1 ? 'file' : 'files'}`
      : commits ? `${commits.totalCount} ${commits.totalCount === 1 ? 'commit' : 'commits'}`
        : empty?.label ?? (displayGitState?.kind === 'pre-merge' ? 'no commits yet' : null);
    const stat = displayLive && (displayLive.totalAdded || displayLive.totalRemoved)
      ? `+${displayLive.totalAdded}/-${displayLive.totalRemoved}`
      : null;
    const tooltip = [
      displayLive ? this.gitTooltip() : null,
      displayGitState?.tooltip ?? null,
      empty?.tooltip ?? null,
    ].filter((part): part is string => !!part).join('\n\n');

    return { kind, label, refIcon, refTooltip, value, summary, stat, tooltip };
  });

  readonly mergeSignal = computed(() => buildMergeSignal(this.job()));

  readonly integrationStatus = computed(() => currentIntegrationStatus(this.job()));
  readonly needsAttention = computed(() => cardNeedsAttention(this.job()));
  readonly outcomeIssueBadge = computed(() => buildOutcomeIssueBadge(this.job()));
  readonly currentPendingIntent = computed(() =>
    this.job().state === TaskState.Progress ? this.job().pendingIntent ?? null : null);
  readonly currentAutoLoop = computed(() =>
    this.job().state === TaskState.Progress ? this.job().autoLoop ?? null : null);
  readonly currentQuotaWait = computed(() =>
    this.job().state === TaskState.Progress ? this.job().quotaWait ?? null : null);
  /**
   * Card-level "code review running" flag. Reads the shared
   * {@link CodeReviewActivityStore} singleton the detail-pane panel marks
   * while a user-triggered review is in flight, so the operator sees the
   * pass progressing on the board even after navigating away from the task
   * (the user's "Progress an die Karte" requirement). Ephemeral: clears when
   * the synchronous review call resolves.
   */
  readonly codeReviewRunning = computed(() => {
    const job = this.job();
    return this.codeReviewActivity.isRunning(
      CodeReviewActivityStore.key(job.watchPath, job.id),
    );
  });

  /** Hot-state threshold: amber pill once the loop is at 80% of the iteration cap. */
  readonly loopHot = computed(() => {
    const al = this.currentAutoLoop();
    if (!al || al.maxIterations <= 0) return false;
    return al.iteration / al.maxIterations >= 0.8;
  });

  loopTooltip(al: AutoLoopSnapshot): string { return buildLoopTooltip(al); }

  pendingTooltip(pi: PendingIntent): string { return buildPendingTooltip(pi); }

  /** Compact tokens label: 850 -> "850", 2400 -> "2.4k", 850000 -> "850k", 3_100_000 -> "3.1M". */
  formatTokens(n: number): string { return formatTokens(n); }

  /**
   * Token-bubble descriptor: returns null when the task has no recorded
   * orchestrator activity (input + output + cacheRead + cacheWrite == 0).
   */
  readonly tokenBubble = computed(() => buildTokenBubble(this.job().tokenSummary));

  readonly agentIcon = computed(() => {
    const t = this.job().cliType;
    return t ? cliTypeIcon(t) : '🤖';
  });

  readonly effectiveModelChip = computed(() => buildEffectiveModelChip(this.job(), this.clients.resolve(this.job().ownerClientId)));

  readonly identity = computed(() => projectIdentity(this.job().projectName));

  /** Epic container card: drives the "EPIC" badge in the title row. */
  readonly isEpic = computed(() => this.job().kind === 'epic');
  readonly inlineEpicSubTasks = computed(() => {
    const job = this.job();
    if (job.kind !== 'epic') return [] as TaskInfo[];
    return this.epicSubTasks()
      .filter((task) => task.kind !== 'epic' && task.epicId === job.id && task.watchPath === job.watchPath)
      .sort((a, b) => {
        const state = (a.state ?? '').localeCompare(b.state ?? '');
        if (state !== 0) return state;
        return (a.title || a.id).localeCompare(b.title || b.id);
      });
  });
  readonly hasInlineEpicSubTasks = computed(() => this.inlineEpicSubTasks().length > 0);

  private readonly epicExpansion = inject(EpicExpansionStore);
  /**
   * Inline epic expand state, read from the shared {@link EpicExpansionStore}
   * keyed on this epic's id rather than a local signal. Keying on the task id
   * (not the component instance) is what keeps the expand open across polling
   * cycles and card re-mounts - see the store's class doc.
   */
  readonly epicExpanded = computed(() => this.epicExpansion.isExpanded(this.job().id));

  /** Parent epic id when this card is a sub-task, else null (drives the "↳ epic" chip). */
  readonly subTaskEpicId = computed(() => {
    const id = this.job().epicId;
    return id && id.trim().length > 0 ? id : null;
  });

  toggleEpicExpanded(event: Event): void {
    event.stopPropagation();
    this.epicExpansion.toggle(this.job().id);
  }

  onInlineSubTaskClick(event: Event, subTask: TaskInfo): void {
    event.stopPropagation();
    this.subTaskClick.emit(subTask);
  }

  laneLabel(state: string): string {
    return stateLabel(state).replace(/-/g, ' ');
  }

  verdictLabel(verdict: TaskInfo['orchestratorVerdict']): string | null {
    return verdict ? verdict.replace(/-/g, ' ') : null;
  }

  readonly isRunning = computed(() =>
    this.job().state === TaskState.Progress && isTaskRunActive(this.job()));
  readonly stalledState = computed(() => deriveStalledTaskState(this.job(), nowTick()));
  /**
   * DtC step 6 CooldownRetry banner. Non-null only while a 3-progress card is
   * holding out its infra-crash re-pickup backoff (`runActivity.failed-backoff`);
   * renders distinctly from the "Running live" chip. Reads the shared `taskCardNow`
   * so the "in Ns" countdown refreshes with every relative-time tick / poll.
   * See {@link buildCooldownRetryBanner}.
   */
  readonly cooldownBanner = computed(() => buildCooldownRetryBanner(this.job(), taskCardNow()));

  /**
   * AGT-2029 waits-on dependency chip from the backend-computed `waitsOn`
   * status (fulfilled/open per target, blocked, cycle). Null when no deps.
   */
  readonly dependencyChip = computed(() => buildVisibleDependencyChip(this.job()));
  readonly providerAuthWait = computed(() => this.providerAuthStatus.loaded()
    ? providerAuthWaitReason(this.job(), this.providerAuthStatus.statuses(), this.providerAuthStatus.links())
    : null);

  readonly relativeActivity = computed(() => {
    const dateStr = this.job().lastActivity;
    if (!dateStr) return 'never';
    const diff = taskCardNow() - new Date(dateStr).getTime();
    const mins = Math.floor(diff / 60000);
    if (mins < 1) return 'just now';
    if (mins < 60) return mins + 'm ago';
    const hrs = Math.floor(mins / 60);
    if (hrs < 24) return hrs + 'h ago';
    return Math.floor(hrs / 24) + 'd ago';
  });

  // Context menu: copy actions + epic assignment (way 2).
  private readonly notifications = inject(NotificationService);
  private readonly jobs = inject(TaskService);
  private readonly selection = inject(TaskSelectionService);
  private readonly boardFilters = inject(BoardFiltersService);
  readonly cardContextMenu = signal<{ x: number; y: number } | null>(null);
  /** Epics in this card's project, loaded on right-click for the assign submenu. */
  private readonly epicsForMenu = signal<EpicRollup[]>([]);

  readonly cardCtxMenuItems = computed(() => buildCardCtxMenuItems(
    this.job(), this.isEpic(), this.epicsForMenu(), this.subTaskEpicId(), this.mutationsBlocked()));

  /** AGT-2029: open the dependency this card is waiting on (see resolveDependencyTarget). */
  navigateToDependency(chip: DependencyChip, event: MouseEvent): void {
    event.preventDefault();
    event.stopPropagation();
    const target = resolveDependencyTarget(chip, this.jobs.jobs());
    if (target) {
      this.selection.openDetail(target);
      return;
    }
    this.notifications.info(
      chip.targetKey
        ? `${chip.targetKey} is not loaded in the current workspace view.`
        : 'That dependency could not be opened.',
    );
  }

  openCardContextMenu(event: MouseEvent): void {
    event.preventDefault();
    event.stopPropagation();
    this.openCardMenuAt(event.clientX, event.clientY);
  }

  /**
   * A11y: keep the card actions (incl. Delete) reachable without a mouse now
   * that the hover trash button is gone. The Menu/Application key and Shift+F10
   * are the platform convention for "open the context menu on the focused
   * element"; we anchor the menu to the focused card's top-left corner.
   */
  onCardKeyDown(event: KeyboardEvent): void {
    const isContextMenuKey = event.key === 'ContextMenu'
      || (event.shiftKey && event.key === 'F10');
    if (!isContextMenuKey) return;
    event.preventDefault();
    event.stopPropagation();
    const rect = this.hostRef.nativeElement.getBoundingClientRect();
    this.openCardMenuAt(rect.left + 12, rect.top + 12);
  }

  private openCardMenuAt(x: number, y: number): void {
    this.cardContextMenu.set({ x, y });
    // Refresh the assign list each open (only for task cards). Best-effort:
    // the section just shows "No epics" on failure.
    if (!this.isEpic() && !this.mutationsBlocked()) {
      const watchPath = this.job().watchPath;
      this.jobs.getEpics().subscribe({
        next: (list) => this.epicsForMenu.set((list ?? []).filter((e) => e.watchPath === watchPath)),
        error: () => this.epicsForMenu.set([]),
      });
    }
  }

  closeCardContextMenu(): void {
    this.cardContextMenu.set(null);
  }

  onCardCtxMenuItemClick(ev: MenuItemClickEvent): void {
    const job = this.job();

    if (ev.id === DELETE_ID) {
      if (this.mutationsBlocked()) return;
      // Same flow as the old hover trash button: emit and let the parent own
      // the confirm/undo prompt. Delete semantics are unchanged.
      this.deleteRequested.emit(job);
      return;
    }
    if (ev.id.startsWith(EPIC_ASSIGN_PREFIX)) {
      if (this.mutationsBlocked()) return;
      const epicId = ev.id.slice(EPIC_ASSIGN_PREFIX.length);
      if (epicId === this.subTaskEpicId()) return; // already in this epic
      this.assignEpic(epicId);
      return;
    }
    if (ev.id === EPIC_DETACH_ID) {
      if (this.mutationsBlocked()) return;
      this.assignEpic(null);
      return;
    }
    if (ev.id === FILTER_DEPENDENTS_ID && job.key) {
      this.boardFilters.setDependsOnFilter(job.key);
      this.notifications.info(`Filtering to tasks that depend on ${job.key}`);
      return;
    }

    let text = '';
    let label = '';
    if (ev.id === 'copy-name') { text = job.title || job.id; label = 'Name'; }
    else if (ev.id === 'copy-id') { text = job.id; label = 'ID'; }
    else if (ev.id === 'copy-key' && job.key) { text = job.key; label = 'Key'; }
    if (text) {
      copyTextToClipboard(text).then(ok => {
        if (ok) this.notifications.success(`${label} copied`);
      });
    }
  }

  /** Way 2: attach (epicId) or detach (null) this task, then refresh the board. */
  private assignEpic(epicId: string | null): void {
    if (this.mutationsBlocked()) return;
    const job = this.job();
    this.jobs.setJobEpic(job.id, epicId, job.watchPath).subscribe({
      next: () => {
        const epic = this.epicsForMenu().find((e) => e.id === epicId);
        this.notifications.success(
          epicId ? `Assigned to epic: ${epic?.title ?? epicId}` : 'Detached from epic',
        );
        this.jobs.refresh(true);
      },
      error: () => this.notifications.error('Could not update epic assignment'),
    });
  }
}
