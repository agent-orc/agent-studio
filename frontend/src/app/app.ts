import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  OnInit,
  signal,
  untracked,
  viewChild,
  ViewChild,
  ViewEncapsulation,
  OnDestroy,
} from '@angular/core';
import { forkJoin } from 'rxjs';
import { FormsModule } from '@angular/forms';
import {
  BoardFiltersService, ActiveBoardFiltersComponent,
  CreateTaskDialogComponent,
  EpicGroupBoardComponent,
  EpicOverviewScreenComponent,
  type EpicOverviewScope,
  FiltersDropdownComponent,
  TaskColumnComponent,
  KanbanFilterSidesheetComponent,
  LaneCollapseService,
  ProjectTabsComponent,
  TypeFilterOption,
  BoardDragStateService,
  BoardMutationsService,
  CreateTaskFormService,
  buildProjectTokenChip,
  flattenGrouped,
  excludeEpics,
  projectAutoInfo,
  projectRunnerIndicator,
  splitReadyByPhase,
} from './features/board';
import {
  TaskDetailComponent,
  DetailLoadErrorComponent,
  TaskDetailLoadSectionsComponent,
  TaskSelectionService,
  TriageController,
  LanePagerService,
  LANE_LABELS,
  overflowActionsFor,
  primaryActionFor,
  mergeAcceptViewFor,
  type MergeAcceptView,
  type TriageButton,
} from './features/task-detail';
import {
  buildComposerLocationContext,
  OrchestratorChatHistoryComponent,
  OrchestratorFeedComponent,
  OrchestratorFeedStore,
  OrchestratorSideSheetComponent,
} from './features/orchestrator';
import {
  DEFAULT_PROJECT_RAIL_KEY,
  ProjectOverlaysComponent,
  ProjectOverlaysService,
  ProjectRailKey,
  ProjectUrlPreviewTabComponent,
  WorkbenchTabHostComponent,
} from './features/project-detail';
import {
  AutoReviewIndicatorComponent,
  StatusBarComponent,
  UiPreferencesService,
  WorkspaceBannerComponent,
  WorkspaceCreateDialogComponent, OnboardProjectDialogComponent, CrashRecoveryPromptComponent,
  WorkspaceManagerService,
  WorkspaceOverlaysComponent,
  WorkspaceOverlaysService,
  type WorkspaceSettingsSection,
} from './features/shell';
import { E2ECleanupDialogComponent, TagManagerDialogComponent } from './features/dev-tools';
import {
  UpdateBlockModalComponent,
  UpdateCenterComponent,
  UpdateVersionBadgeComponent,
} from './features/update';
// UpdateBannerComponent removed in F56; update notifications now flow through
// UpdateNotificationBridge → NotificationService → notification-stack toasts.
import { VerboseDebugOverlayComponent } from './features/verbose-debug';
import {
  StudioShellComponent,
  ProjectHubViewComponent,
  StudioDiffViewComponent,
  StudioActivityViewComponent,
  ProjectHubUrlService,
  StudioTabStateService,
  StudioPanelStateService,
  studioTabKey,
  parseStudioRoute,
  navigateStudioRoute,
  replaceTaskViewRoute,
  studioProjectSlug,
  studioRouteForTab,
  type StudioTab,
  type TaskDetailRouteTab,
  type TaskInspectorRouteTab,
} from './features/studio-shell';
import { TaskService } from './services/task.service';
import { ClientService } from './services/client.service';
import { NotificationService } from './services/notification.service';
import type { TaskDetail, TaskInfo, WatchPathEntry, CliType } from './models/task.model';
import { CLI_TYPES, TaskState } from './models/task.model';
import { ErrorDialogService } from './services/error-dialog.service';
import {
  cliTypeLabel as fmtCliTypeLabel,
  formatMultiplier as fmtMultiplier,
  stateLabel as fmtStateLabel,
} from './services/format.util';
import { ErrorDialogComponent } from './components/error-dialog/error-dialog.component';
import { ConfirmDialogComponent } from './components/app-dialog/confirm-dialog/confirm-dialog.component';
import { StudioIconComponent } from './components/studio-icon/studio-icon.component';
import { NotificationStackComponent } from './components/app-dialog/notification-stack/notification-stack.component';
import { MediaLightboxComponent } from './components/media-lightbox/media-lightbox.component';
import { OfflineBannerComponent } from './components/offline-banner/offline-banner.component';
import { PublicDemoBannerComponent } from './components/public-demo-banner/public-demo-banner.component';
import { UpdateClientService } from './services/update.service';
import { UpdateNotificationBridge } from './services/update-notification-bridge.service';
import { projectIdentity } from './services/project-identity.util';
import { displayStateToLaneKey, allowsDragReorder } from './services/lane-sort.util';
import { buildRunActivityBadge, freshestRunInfo } from './services/run-activity.util';
import { NowTickService } from './services/now-tick.service';
import { PageContextService } from './services/page-context.service';
import { DevToolsService } from './services/dev-tools.service';
import { FeatureFlagsService } from './services/feature-flags.service';
import { TaskCompletionSoundService } from './services/task-completion-sound.service';
import { TagRegistryStore } from './services/tag-registry.store';
import { CliCatalogStore } from './features/cli';
import type { CliOutputLine } from './models/task.model';
import type { RunTimeline } from './features/run-timeline';
import type { TaskScreenshot } from './features/screenshots';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { MenuComponent, MenuItem, MenuItemClickEvent } from './components/menu';
import { CostBreakdownDialogComponent, type TaskTokenSummary } from './features/tokens'; // verbose-debug overlay context types
import { LoadingSurfaceComponent, PendingButtonDirective } from './components/async-feedback';
import { AuthGateComponent, AuthService } from './components/auth-gate/auth-gate';
import { ExecutionLocationBadgeComponent } from './components/execution-location-badge/execution-location-badge.component';
interface VerboseDebugContext {
  lines: CliOutputLine[];
  runTimeline: RunTimeline | null;
  screenshots: TaskScreenshot[];
  tokenSummary: TaskTokenSummary | null;
  job: TaskInfo | null;
}
interface ShellPanesVisible {
  prompt: boolean;
  protocol: boolean;
  git: boolean;
}
const SHELL_PANES_FALLBACK: ShellPanesVisible = {
  prompt: false,
  protocol: false,
  git: false,
};
@Component({
  selector: 'app-root',
  imports: [
    TaskColumnComponent,
    TaskDetailComponent,
    DetailLoadErrorComponent,
    TaskDetailLoadSectionsComponent,
    OrchestratorSideSheetComponent,
    OrchestratorChatHistoryComponent,
    OrchestratorFeedComponent,
    ProjectOverlaysComponent,
    AutoReviewIndicatorComponent,
    StatusBarComponent,
    FormsModule,
    CreateTaskDialogComponent,
    EpicGroupBoardComponent,
    ErrorDialogComponent,
    ConfirmDialogComponent,
    NotificationStackComponent,
    MediaLightboxComponent,
    OfflineBannerComponent,
    PublicDemoBannerComponent,
    ProjectTabsComponent,
    E2ECleanupDialogComponent,
    TagManagerDialogComponent,
    WorkspaceOverlaysComponent,
    WorkspaceBannerComponent,
    WorkspaceCreateDialogComponent, OnboardProjectDialogComponent, CrashRecoveryPromptComponent,
    UpdateVersionBadgeComponent,
    UpdateCenterComponent,
    UpdateBlockModalComponent,
    VerboseDebugOverlayComponent,
    FiltersDropdownComponent,
    KanbanFilterSidesheetComponent, ActiveBoardFiltersComponent,
    TooltipDirective,
    MenuComponent,
    EpicOverviewScreenComponent,
    StudioShellComponent,
    ProjectHubViewComponent,
    StudioDiffViewComponent,
    StudioActivityViewComponent,
    LoadingSurfaceComponent,
    PendingButtonDirective,
    CostBreakdownDialogComponent,
    ProjectUrlPreviewTabComponent,
    WorkbenchTabHostComponent,
    StudioIconComponent,
    AuthGateComponent,
    ExecutionLocationBadgeComponent,
  ],
  // Cycle 7b: OnPush. The shell mounts kanban + detail panel + many
  // sheets; default (Default) change detection re-checked the whole
  // tree on every async event (every poll tick, every signal write).
  // OnPush means CD only runs when an @Input changes (signals already
  // mark themselves dirty), or an event handler in the template fires.
  // The board sub-tree was already covered indirectly by TaskCard's
  // OnPush; promoting the shell ensures the sibling sheets and the
  // header don't trigger whole-app passes during a 2 s grouped poll.
  changeDetection: ChangeDetectionStrategy.OnPush,
  // Keep styles global to this subtree — the App shell still owns the
  // .header*, .filter-chip*, .overlay*, .create-dialog*, .error-dialog*
  // class rules used by the extracted dialogs and project-tabs.
  encapsulation: ViewEncapsulation.None,
  templateUrl: './app.html',
  styleUrl: './app.scss',
})
export class App implements OnInit, OnDestroy {
  readonly auth = inject(AuthService); private studioInitialized = false;
  readonly jobService = inject(TaskService);
  readonly errorDialog = inject(ErrorDialogService);
  readonly devTools = inject(DevToolsService);
  readonly clientService = inject(ClientService);
  private readonly notifications = inject(NotificationService);
  readonly featureFlags = inject(FeatureFlagsService);
  readonly projectHubUrls = inject(ProjectHubUrlService);
  private readonly orchestratorFeedStore = inject(OrchestratorFeedStore);
  private readonly _completionSound = inject(TaskCompletionSoundService);
  readonly updateClient = inject(UpdateClientService);
  private readonly _updateBridge = inject(UpdateNotificationBridge);
  readonly studioTabState = inject(StudioTabStateService);
  readonly pageContext = inject(PageContextService);
  readonly routeDetailTab = signal<TaskDetailRouteTab | null>(null);
  readonly routeInspectorTab = signal<TaskInspectorRouteTab | null>(null);
  readonly studioRouteReady = signal(false);
  private pendingStudioTaskReference: string | null = null;
  readonly orchestratorComposerContext = computed(() => buildComposerLocationContext(
    this.studioTabState.activeTab(),
    this.jobService.jobs(),
  ));
  private readonly studioPanelState = inject(StudioPanelStateService);
  private readonly nowTick = inject(NowTickService).now;

  /** Task selection state lives in TaskSelectionService; handlers stay here. */
  private readonly jobSelection = inject(TaskSelectionService);
  private readonly lanePager = inject(LanePagerService);
  readonly selectedJob = this.jobSelection.selected;
  readonly detailPreview = this.jobSelection.detailPreview;
  readonly boardLoading = this.jobService.loading;
  readonly detailLoading = this.jobSelection.detailLoading;
  readonly detailLoadError = this.jobSelection.detailLoadError;
  readonly triageToast = this.jobSelection.triageToast;
  // ASS-1751: run-activity pill for the slim studio tab-bar header. The
  // studio shell hides <app-detail-header>, so the open task's run state is
  // surfaced here (the kanban side-panel keeps its own header pill).
  // AGT-2378: read through to the live board entry — the detail snapshot is
  // frozen at open time and would pin the pill on "kein aktiver Run". The live
  // entry only wins when it is at least as fresh, so a mutation just applied to
  // the open task does not flicker back — see `freshestRunInfo`.
  readonly studioRunActivityBadge = computed(() => {
    const detail = this.selectedJob();
    if (!detail) return null;
    const info = freshestRunInfo(detail.info, this.jobService.jobs());
    return buildRunActivityBadge(info, this.nowTick());
  });
  readonly epicTabTaskDetail = signal<TaskDetail | null>(null);
  readonly epicTabSubTaskPeers = computed<TaskInfo[]>(() => {
    const epic = this.selectedJob();
    if (this.studioTabState.activeTab()?.kind !== 'epic') return [];
    if (!epic) return [];
    const laneRank = new Map(Object.keys(LANE_LABELS).map((state, index) => [state, index]));
    return this.jobService.jobs()
      .filter((job) =>
        job.kind !== 'epic' &&
        job.epicId === epic.info.id &&
        job.watchPath === epic.info.watchPath
      )
      .sort((a, b) => {
        const stateDelta = (laneRank.get(a.state) ?? 999) - (laneRank.get(b.state) ?? 999);
        if (stateDelta !== 0) return stateDelta;
        const orderDelta = a.order - b.order;
        if (orderDelta !== 0) return orderDelta;
        return (a.title || a.id).localeCompare(b.title || b.id);
      });
  });

  @ViewChild('jobDetail') private jobDetailRef?: TaskDetailComponent;
  @ViewChild('orchSideSheet') private orchSideSheetRef?: OrchestratorSideSheetComponent;
  private readonly jobDetailSig = viewChild<TaskDetailComponent>('jobDetail');
  readonly studioTriageActingId = computed(() => this.jobDetailSig()?.triageActingId() ?? null);
  readonly shellPanesVisible = computed<ShellPanesVisible>(
    () => this.jobDetailSig()?.panesVisible() ?? SHELL_PANES_FALLBACK,
  );
  /**
   * Signal-form view child for the orchestrator side-sheet. Lets the
   * `effectiveCompactCards` computed react when the rail opens/closes
   * (F4) without a manual subscription bridge. The legacy @ViewChild
   * above stays for the imperative `toggle()` call sites.
   */
  private readonly orchSideSheetSig = viewChild<OrchestratorSideSheetComponent>('orchSideSheet');
  /** Records the lane the user was triaging in. When the open job's state
   *  diverges from this (e.g. an external client moved it) we treat that as
   *  an auto-advance and toast accordingly. */
  private triageLaneState: string | null = null;
  /**
   * Whether the studio active tab the active-tab→selection effect last saw
   * was a task. Lets that effect distinguish "user navigated away FROM a task
   * tab" (strip the stale task route, even if the detail fetch is still in flight)
   * from "cold boot landed on a non-task tab that carries a task deep link"
   * (leave it for `restoreFromUrl`). Without it, a fast task→board switch made
   * before the detail resolved would keep the stale param and let the late
   * fetch yank the user back onto the task — the very F5 symptom, mid-session.
   */
  private studioActiveTabWasTask = false;
  /**
   * Set for one selection-change tick when the user steps through the lane
   * via the pager / cursor keys (j/k/arrows/Prev/Next). The studio-shell
   * mirror effect consumes it to RETARGET the active task tab in place
   * rather than opening a fresh tab per step — so walking a lane reloads
   * the one tab instead of leaving a trail of them.
   */
  private laneNavRetarget = false;
  private relatedOpenToken = 0;
  /**
   * Read-only "Verbose Debug" overlay state opened from the orchestrator
   * side sheet's bug button. The protocol pane has its own copy that lives
   * inside the task chat workbench and reuses the live polling services;
   * this app-shell instance is the lazy-fetched escape hatch for the
   * project side sheet's "🐞" affordance, which is reachable even when the
   * task detail isn't currently displayed.
   */
  readonly verboseDebugContext = signal<VerboseDebugContext | null>(null);
  /**
   * Cycle 10a: create-job dialog state + open/cancel/submit logic
   * lives in CreateTaskFormService. The shell re-exposes the visibility
   * signal + the bound fields via getters so the existing template
   * bindings keep working unchanged.
   */
  readonly createJobForm = inject(CreateTaskFormService);
  /** Cycle 10b: board-mutation handlers (drag/drop, reorder, delete, archive, etc.) live here. */
  private readonly boardMutations = inject(BoardMutationsService);
  private readonly boardDrag = inject(BoardDragStateService);
  /** Re-exposed for the column template so the Archive-all button can disable
   *  itself + show a spinner while a bulk archive is in flight. */
  readonly archivingInProgress = this.boardMutations.archiving;
  /** Cycle 10c: triage panel + j/k navigation + auto-advance live here. */
  private readonly triage = inject(TriageController);
  readonly showCreate = this.createJobForm.visible;
  readonly availableModels = this.createJobForm.availableModels;
  /**
   * Cycle 9g: per-project overlay state (orch-feed / project-shell /
   * analysis-report) lives in ProjectOverlaysService.
   * The shell re-exposes the read signals so existing template guards +
   * keyboard guards work unchanged; the `<app-project-overlays />`
   * container owns the rendering.
   */
  private readonly projectOverlays = inject(ProjectOverlaysService);
  readonly orchFeedProject = this.projectOverlays.orchFeedProject;
  readonly projectShellName = this.projectOverlays.projectShellName;
  readonly projectShellRail = this.projectOverlays.projectShellRail;
  readonly analysisReportFocus = this.projectOverlays.analysisReportFocus;
  /**
   * Cycle 9g: workspace overlay state (tokens / screenshots / cli-admin)
   * lives in WorkspaceOverlaysService. The shell re-exposes the read
   * signals so the existing template guards keep working unchanged; the
   * `<app-workspace-overlays />` container owns the actual rendering.
   */
  private readonly workspaceOverlays = inject(WorkspaceOverlaysService);
  /**
   * Owns the create-workspace modal visibility. Public so the template
   * can bind <code>workspaceManager.createOpen()</code> for the
   * <code>@if</code> guard around <code>&lt;app-workspace-create-dialog&gt;</code>;
   * the studio-shell calls into it via <code>openCreate()</code> when
   * the "+ Add workspace" affordance fires.
   */
  readonly workspaceManager = inject(WorkspaceManagerService);
  readonly workspaceTokensOpen = this.workspaceOverlays.tokensOpen;
  readonly workspaceScreenshotsOpen = this.workspaceOverlays.screenshotsOpen;
  readonly cliAdminOpen = this.workspaceOverlays.cliAdminOpen;
  /**
   * Drives the status-bar Settings button's pressed/active state. Excludes
   * the 'caps' (CLI usage) section in both layouts: that section owns the
   * separate "Usage" pill, and letting Settings light up for it too would
   * put the single `--studio-accent` active fill on two rail items at once
   * (see WorkspaceOverlaysService.anyOpenExceptUsage).
   */
  readonly workspaceSettingsOpen = computed(() => this.featureFlags.vsCodeLayout()
    ? this.studioTabState.activeTab()?.kind === 'workspace-settings'
      && this.workspaceOverlays.section() !== 'caps'
    : this.workspaceOverlays.anyOpenExceptUsage());
  private hashListener: (() => void) | null = null;
  private kanbanKeyListener: ((ev: KeyboardEvent) => void) | null = null;
  readonly watchPaths = signal<WatchPathEntry[]>([]);
  /**
   * Cycle 9 / ADR-0034: search query, four faceted filters, URL hash +
   * query-param round-trip, and `filteredGrouped` derivation all live
   * in BoardFiltersService (features/board/state/board-filters.service.ts).
   * The shell re-exposes the same signal/computed/method names so
   * existing template bindings keep working unchanged.
   */
  private readonly boardFilters = inject(BoardFiltersService);
  private readonly tagRegistryStore = inject(TagRegistryStore);
  private readonly cliCatalogStore = inject(CliCatalogStore);
  readonly activeProjects = this.boardFilters.activeProjects;
  /** Active project names as a plain readonly array for the workspace banner input. */
  readonly bannerProjects = this.boardFilters.bannerProjects;
  // Cycle 9: side-sheet width owned by UiPreferencesService.
  private readonly uiPrefs = inject(UiPreferencesService);
  readonly sideSheetWidth = this.uiPrefs.sideSheetWidth;
  readonly collapsedGroups = signal<Set<string>>(
    new Set(JSON.parse(localStorage.getItem('collapsedGroups') ?? '[]')),
  );
  /**
   * Per-lane collapse preference for the main board. Values are state ids
   * (`1-preparation` … `7-archive`); a state present here renders as a
   * narrow rail instead of a full column. Persisted in localStorage so the
   * user's layout survives reloads. Default is empty (everything expanded)
   * to keep the first-run board useful before any customisation.
   */
  /**
   * Cycle 9 / ADR-0034: lane collapse and container focus state live in
   * LaneCollapseService (features/board/state/lane-collapse.service.ts).
   * The shell exposes the same `collapsedLanes` and `focusedContainer`
   * signal references so existing template bindings and computeds keep
   * working unchanged. Methods further down delegate to the service.
   */
  private readonly laneCollapse = inject(LaneCollapseService);
  readonly collapsedLanes = this.laneCollapse.collapsedLanes;
  readonly focusedContainer = this.laneCollapse.focusedContainer;
  readonly taskNavCollapsed = this.uiPrefs.taskNavCollapsed;

  /**
   * Board "Gruppieren nach Epic" toggle. When on, the board case renders the
   * epic tree instead of the lane columns. The tree is built from the same
   * `filteredGrouped` feed (via `epicBoardTasks`) so search + filters apply to
   * both shapes identically. Persisted in UiPreferencesService.
   */
  readonly groupByEpic = this.uiPrefs.groupByEpic;
  toggleGroupByEpic(): void {
    this.uiPrefs.toggleGroupByEpic();
  }
  /**
   * Flat, de-duplicated task list for the epic tree. Sourced from the filtered
   * lane feed so the "Group by epic" view honours the active search + filters;
   * `flattenGrouped` collapses the `review`/`autoReview` legacy alias so no card
   * is counted twice.
   */
  readonly epicBoardTasks = computed(() => flattenGrouped(this.filteredGrouped()));

  /**
   * Card density was abolished (AGT-2035): job cards always render full. This
   * stays as a constant `false` so the remaining `[compact]` bindings resolve
   * to full rendering without threading the removed preference everywhere.
   */
  readonly effectiveCompactCards = computed<boolean>(() => false);
  readonly showE2ECleanup = signal(false);
  readonly showTagManager = signal(false);
  readonly devToolsMenuOpen = signal(false);
  /**
   * Free-text query for the kanban search box. Matched as a case-insensitive
   * substring across every TaskInfo field that's loaded for the grouped view
   * (title, id, project, agent, model, CLI, session, state, owner, phase,
   * type, tag ids). Prompt-body text is intentionally not searched here -
   * grouped jobs don't carry their prompts, so a "matches body" pretence
   * would lie. Ephemeral; not persisted to localStorage.
   */
  // Cycle 9 / ADR-0034: filter state + URL sync delegated to BoardFiltersService.
  readonly searchQuery = this.boardFilters.searchQuery;
  readonly filterBadgeCount = computed(() => this.boardFilters.activeFilterCount());
  readonly hasActiveFiltersOrSearch = this.boardFilters.hasActiveFiltersOrSearch;
  readonly filteredGrouped = this.boardFilters.filteredGrouped;
  readonly filteredTaskCount = this.boardFilters.filteredTaskCount;
  readonly totalTaskCount = this.boardFilters.totalTaskCount;

  onSidesheetClearAll(): void {
    this.boardFilters.clearSearchAndFilters();
  }

  /**
   * F25: opens the activity-bar Filters panel and focuses the search
   * input inside the inline filter UI. Bound to the `/` keyboard
   * shortcut, which previously toggled the right-edge filter sheet
   * before the sheet was collapsed into a single source-of-truth
   * activity-bar panel.
   */
  private openFiltersPanelAndFocusSearch(): void {
    if (this.studioPanelState.active() !== 'filters' || !this.studioPanelState.visible()) {
      this.studioPanelState.toggle('filters');
      if (!this.studioPanelState.visible()) {
        this.studioPanelState.setVisible(true);
      }
    }
    queueMicrotask(() => {
      const input = document.querySelector<HTMLInputElement>(
        '[data-testid="kanban-filter-sidesheet-search"]',
      );
      input?.focus();
      input?.select();
    });
  }

  readonly projectNames = computed(() => {
    return this.watchPaths().map((wp) => wp.name);
  });

  // Cycle 9 / ADR-0034: filter signals + URL sync delegated to BoardFiltersService.
  // The shell re-exposes the same names so existing template bindings + call
  // sites keep working unchanged.
  readonly activeClientFilter = this.boardFilters.activeClientFilter;
  readonly activeTypeFilter = this.boardFilters.activeTypeFilter;
  readonly activeTagFilter = this.boardFilters.activeTagFilter;
  readonly activeType = this.boardFilters.activeType;
  readonly hasActiveFilters = this.boardFilters.hasActiveFilters;
  /** Workspace tag registry, refreshed on init via `loadTagRegistry`. */
  readonly tagRegistry = this.tagRegistryStore.tags;
  readonly tagRegistryById = this.tagRegistryStore.byId;

  /** Static option list for the type filter dropdown. */
  readonly typeFilterOptions: readonly TypeFilterOption[] = [
    { value: 'bug', label: 'Bugs', icon: '🐞', kind: 'bug' },
    { value: 'feature', label: 'Features', icon: '✨', kind: 'feature' },
    { value: 'chore', label: 'Chores', icon: '·', kind: 'chore' },
  ];

  setClientFilter(id: string | null): void {
    this.boardFilters.setClientFilter(id);
  }
  clientFilterChange(event: Event): string | null {
    return this.boardFilters.clientFilterChange(event);
  }
  clearTypeFilters(): void {
    this.boardFilters.clearTypeFilters();
  }
  onSetType(type: string | null): void {
    this.boardFilters.onSetType(type);
  }
  toggleTypeFilter(type: string): void {
    this.boardFilters.toggleTypeFilter(type);
  }
  toggleTagFilter(id: string): void {
    this.boardFilters.toggleTagFilter(id);
  }
  loadTagRegistry(): void {
    this.jobService.listTags().subscribe({
      next: (tags) => this.tagRegistryStore.set(tags),
      error: () => this.tagRegistryStore.set([]),
    });
  }

  // The visible lane order is the canonical Order field, which is also what
  // ProjectRunner.GetNextReadyJob picks by. Keeping a single source of truth
  // here means "what's at the top of Ready runs first" is structurally true,
  // not just usually true.
  // Board display contract: epics are containers, not board work-items, so the
  // flat lane board (focusGroups / laneGroups) renders tasks only. The
  // "Group by epic" view binds the unfiltered `epicBoardTasks` feed instead, so
  // epics still surface there and via the Epic navigation. See `excludeEpics`.
  readonly displayGrouped = computed(() => excludeEpics(this.filteredGrouped()));

  readonly focusGroups = computed(() => {
    const grouped = this.displayGrouped();
    // ADR-0025: seven lanes. The robot icon is the orchestrator's machine
    // pass; the eye icon is the user's "needs me" lane.
    // Orchestrator prep is no longer a backlog lane: it now runs in-place on
    // 1-preparation as the optional pipeline step `pre-orchestrator-prep`
    // (see PipelineCatalogue), so the retired 1a lane is not rendered.
    // Backlog-lane spec: 0-backlog leads the focus list when populated.
    const lanes: { state: string; title: string; icon: string; jobs: TaskInfo[] }[] = [
      { state: TaskState.Backlog, title: 'Backlog', icon: '🗒️', jobs: grouped.backlog ?? [] },
      { state: TaskState.Preparation, title: 'In Preparation', icon: '📋', jobs: grouped.preparation },
    ];
    lanes.push(
      { state: TaskState.Ready, title: 'Ready', icon: '📦', jobs: grouped.ready },
      { state: TaskState.Progress, title: 'In Progress', icon: '🔵', jobs: grouped.progress },
    );
    // 3b-code-not-complete: hide-when-empty park lane.
    if ((grouped.codeNotComplete ?? []).length > 0) {
      lanes.push({
        state: TaskState.CodeNotComplete,
        title: 'Code not complete',
        icon: '🚧',
        jobs: grouped.codeNotComplete,
      });
    }
    lanes.push(
      { state: TaskState.AutoReview, title: 'Post Processing', icon: '🤖', jobs: grouped.autoReview },
      { state: TaskState.Escalated, title: 'Escalated', icon: '⚠️', jobs: grouped.escalated ?? [] },
      { state: TaskState.HumanReview, title: 'Review', icon: '👁️', jobs: grouped.humanReview },
      { state: TaskState.Completed, title: 'Delivered', icon: '🟢', jobs: grouped.completed },
      { state: TaskState.Archive, title: 'Archive', icon: '🗄️', jobs: grouped.archive ?? [] },
    );
    return lanes;
  });

  /**
   * Board lane groups. Three contiguous containers map the workflow:
   *
   *  - backlog: 0-backlog, 1-preparation, 2-ready
   *  - active:  3-progress, 4-auto-review
   *  - decide:  5e-escalated, 5-human-review, 6-completed, 7-archive ("Done & Decide" -
   *             intervention precedes acceptance in the user-owned tail.)
   *
   * The previous human/agent axis suffix was misleading (Backlog mixes
   * agent prep with human triage) and is removed.
   */
  readonly laneGroups = computed(() => {
    const grouped = this.displayGrouped();
    // Backlog lanes put the most actionable work first; the old order buried
    // Ready under large backlogs.
    //   1. 2-ready      "Ready"              — pick-up candidates
    //   2. 1-preparation                     — in human preparation
    //   3. 0-backlog                         — fresh inbox / triage
    const readySplit = splitReadyByPhase(grouped.ready);
    const backlogLanes: { state: string; title: string; icon: string; jobs: TaskInfo[] }[] = [];
    backlogLanes.push({
      state: TaskState.Ready,
      title: 'Ready',
      icon: '📦',
      jobs: readySplit.humanReady,
    });
    if (readySplit.intake.length > 0) {
      // Own "Preparation" lane: only pushed (so only rendered) while the
      // orchestrator-prep/intake loop is actually working a card, so the lane
      // is hidden whenever nothing is mid-preparation.
      backlogLanes.push({
        state: '2-ready-intake',
        title: 'Preparation',
        icon: '🛂',
        jobs: readySplit.intake,
      });
    }
    backlogLanes.push({
      state: TaskState.Preparation,
      title: 'In Preparation',
      icon: '📋',
      jobs: grouped.preparation,
    });
    backlogLanes.push({
      state: TaskState.Backlog,
      title: 'Backlog',
      icon: '🗒️',
      jobs: grouped.backlog ?? [],
    });
    const activeLanes: { state: string; title: string; icon: string; jobs: TaskInfo[] }[] = [
      { state: TaskState.Progress, title: 'In Progress', icon: '🔵', jobs: grouped.progress },
    ];
    // 3b-code-not-complete is a hide-when-empty park lane: the runner moves a
    // task here when it exhausts its auto-pickup retry budget without reaching
    // review, and keeps auto-mode
    // running. It sits at 3-progress / before review so the operator sees stuck
    // work next to what is actively running.
    if ((grouped.codeNotComplete ?? []).length > 0) {
      activeLanes.push({
        state: TaskState.CodeNotComplete,
        title: 'Code not complete',
        icon: '🚧',
        jobs: grouped.codeNotComplete,
      });
    }
    activeLanes.push({
      state: TaskState.AutoReview,
      title: 'Post Processing',
      icon: '🤖',
      jobs: grouped.autoReview,
    });
    const escalatedJobs = grouped.escalated ?? [];
    // Future option: metadata could apply this empty-lane policy to exception
    // lanes such as 1-preparation. For now it is intentionally Escalated-only.
    const showEscalated = escalatedJobs.length > 0 || this.boardDrag.active();
    return [
      {
        id: 'backlog',
        label: 'Backlog',
        lanes: backlogLanes,
      },
      {
        id: 'active',
        label: 'Active',
        lanes: activeLanes,
      },
      {
        id: 'decide',
        label: 'Done & Decide',
        lanes: [
          // Intervention comes before acceptance in the visible workflow.
          ...(showEscalated ? [{ state: TaskState.Escalated, title: 'Escalated', icon: '⚠️', jobs: escalatedJobs }] : []),
          { state: TaskState.HumanReview, title: 'Review', icon: '👁️', jobs: grouped.humanReview },
          { state: TaskState.Completed, title: 'Delivered', icon: '🟢', jobs: grouped.completed },
          { state: TaskState.Archive, title: 'Archive', icon: '🗄️', jobs: grouped.archive ?? [] },
        ],
      },
    ];
  });
  // Copilot removed: no CLI exposes the inline path/token config card, so the
  // error-dialog "Open CLI config" affordance is permanently disabled.
  readonly selectedJobUsesCopilot = computed(() => false);

  // Cycle 9j: triageLanePeers lives in TaskSelectionService.
  readonly triageLanePeers = this.jobSelection.triageLanePeers;

  /** Position (1-based) + total used by the slim-tab pager next to the
   *  prev/next arrows. Reads the lane-pager SNAPSHOT (not the live lane
   *  peers) so the count stays stable when the open task is moved to a
   *  different lane via the overflow menu (ASS-661 req 4): the snapshot's
   *  lane is fixed at capture, and `advanceAfterMutation` drops the moved
   *  task + advances within that same lane, so the pager never jumps to
   *  the destination lane's count. 0 (rendered "—") when the open task has
   *  left the captured iteration. */
  readonly slimPagerPosition = computed<number>(() => {
    const snap = this.lanePager.snapshot();
    const job = this.selectedJob();
    if (!snap || !job) return 0;
    const idx = snap.jobs.findIndex((j) => j.taskKey === job.info.taskKey);
    return idx >= 0 ? idx + 1 : 0;
  });
  readonly slimPagerTotal = computed<number>(() => this.lanePager.total());

  /** Lane the pager iterates (snapshot lane, fallback to the open job's
   *  state). Drives the slim header's navigation-only lane dropdown — it
   *  shows which lane Prev/Next pages through, not the open job's live
   *  state (the two diverge after an external lane change). */
  readonly studioPagerLaneState = computed<string>(
    () => this.lanePager.snapshot()?.lane ?? this.selectedJob()?.info.state ?? '',
  );

  /**
   * Lane options for the slim studio header's lane dropdown. The studio
   * shell hides the projected <app-detail-header> (which owns the kanban
   * detail's own lane select), so this surfaces the navigation-only lane
   * picker in the tab-bar header the user actually sees. Order/labels mirror
   * DetailHeaderComponent.laneOptions: the orchestrator-controlled lanes
   * (3-progress, 4-auto-review) are omitted — they are not manual navigation
   * targets, matching the context menu that refuses them as move targets.
   */
  readonly studioLaneOptions: readonly { state: string; label: string }[] = [
    { state: TaskState.Preparation,   label: 'Preparation' },
    { state: TaskState.Ready,         label: 'Ready' },
    { state: TaskState.Escalated,     label: 'Escalated' },
    { state: TaskState.HumanReview,   label: 'Review' },
    { state: TaskState.Completed,     label: 'Delivered' },
    { state: TaskState.Archive,       label: 'Archive' },
  ];

  isStandardLane(state: string): boolean {
    return this.studioLaneOptions.some((o) => o.state === state);
  }

  stateLabel(state: string): string {
    return fmtStateLabel(state);
  }

  /**
   * Slim-header lane dropdown change → navigation only (ASS-661). Re-points
   * the pager at the chosen lane and opens a task in it; the current task is
   * never moved (lane moves live in the overflow context menu). Re-syncs the
   * native control to the real pager lane afterward: `navigateToLane`
   * captures the snapshot synchronously, so by the microtask the lane signal
   * reflects the landed lane; when navigation is declined (empty lane) it
   * stays put, snapping the <select> back off the user's transient pick.
   */
  onStudioLaneChange(info: TaskInfo, event: Event): void {
    const target = event.target as HTMLSelectElement;
    const next = target.value;
    const current = this.studioPagerLaneState() || info.state;
    queueMicrotask(() => { target.value = this.studioPagerLaneState() || info.state; });
    if (!next || next === current) return;
    this.jobSelection.navigateToLane(next);
  }

  // Cycle 10a: form state (newTitle/newPrompt/newCliType/etc.) lives in
  // CreateTaskFormService. Pass-through getters keep the existing
  // template `[(ngModel)]` and helper-method bindings working unchanged.
  readonly cliTypes = CLI_TYPES;

  cliTypeLabel(t: CliType): string {
    return fmtCliTypeLabel(t);
  }
  formatMultiplier(mult: number | null): string {
    return fmtMultiplier(mult);
  }
  onCreateCliTypeChange(t: CliType): void {
    this.createJobForm.onCreateCliTypeChange(t);
  }
  onDefaultCliChange(t: CliType): void {
    void t;
    this.createJobForm.applyStoredCliDefault();
  }
  onDefaultModelChange(ev: { cliType: CliType; model: string; thinkingLevel: string | null }): void {
    this.createJobForm.onDefaultModelChange(ev);
  }
  canAddTaskToGroup(state: string): boolean {
    return this.createJobForm.canAddTaskToGroup(state);
  }

  readonly devToolsFlags = computed(() => this.devTools.flags());

  /**
   * F23: typed menu-item list driving the shared <app-menu> in the header.
   * Replaces the inline button-per-row markup that lived directly in
   * app.html (and its companion .devtools-menu* SCSS block).
   */
  readonly devtoolsMenuItems = computed<readonly MenuItem[]>(() => {
    const flags = this.devToolsFlags();
    const items: MenuItem[] = [
      { kind: 'header', label: 'System' },
      {
        kind: 'row',
        id: 'orch-config',
        label: 'Orchestrator config',
        hint: 'supervisor + meta-cycle flags',
      },
      {
        kind: 'row',
        id: 'tag-manager',
        label: 'Tag manager',
        hint: 'add, edit, and remove registry tags',
      },
    ];
    if (flags.updateStableEnabled || flags.deleteE2EJobsEnabled) {
      items.push({ kind: 'header', label: 'Dev tools' });
    }
    if (flags.updateStableEnabled) {
      items.push({
        kind: 'row',
        id: 'update-stable',
        label: 'Update Stable',
        hint: 'open resilient update center',
      });
    }
    if (flags.deleteE2EJobsEnabled) {
      items.push({
        kind: 'row',
        id: 'delete-e2e',
        label: 'Delete E2E Tasks',
        hint: 'across all projects',
        danger: true,
      });
    }
    return items;
  });

  onDevtoolsMenuItemClick(ev: MenuItemClickEvent): void {
    switch (ev.id) {
      case 'orch-config':
        this.onPickOrchestratorConfig();
        break;
      case 'tag-manager':
        this.onPickTagManager();
        break;
      case 'update-stable':
        this.onPickUpdateStable();
        break;
      case 'delete-e2e':
        this.onPickDeleteE2E();
        break;
    }
  }

  onPickUpdateStable(): void {
    this.devToolsMenuOpen.set(false);
    this.updateClient.openCenter();
    void this.updateClient.refreshNow();
  }

  /**
   * Slim detail-header proxies — the studio tab-bar surfaces a few
   * task-tab actions that live on the embedded TaskDetailComponent.
   * Forward the click through the ViewChild so the action runs on the
   * same component instance the user is looking at.
   */
  onShellTogglePane(pane: 'prompt' | 'protocol' | 'git'): void {
    this.jobDetailRef?.togglePane(pane);
  }

  onPickDeleteE2E(): void {
    this.devToolsMenuOpen.set(false);
    this.showE2ECleanup.set(true);
  }

  onPickTagManager(): void {
    this.devToolsMenuOpen.set(false);
    this.showTagManager.set(true);
  }

  onTagManagerClosed(): void {
    this.showTagManager.set(false);
    // Pick up any out-of-band changes (e.g. a deletion that was reported
    // back as a banner after the dialog had already started rendering)
    // by refreshing the store from the server.
    this.loadTagRegistry();
  }

  onPickOrchestratorConfig(): void {
    this.devToolsMenuOpen.set(false);
    this.openOrchestratorSettings();
  }

  /**
   * AGT-1812: the standalone Orchestrator-settings modal was retired. The header
   * Dev-tools "Orchestrator config" entry and the orchestrator side-sheet gear
   * now open the platform-global lifecycle flags as the "Orchestrator" section of
   * the one consolidated Settings view (Global group).
   */
  openOrchestratorSettings(): void {
    if (this.featureFlags.vsCodeLayout()) {
      this.openWorkspaceSettingsInStudio('orchestrator');
      return;
    }
    this.workspaceOverlays.openOrchestrator();
  }

  /**
   * F2: id of a job that was just created via the +Add dialog. Lane
   * cards binding this signal render a brief highlight pulse + scroll
   * themselves into view so a new task isn't lost on a 200+ card board.
   * Cleared automatically after one animation cycle.
   */
  readonly justCreatedJobId = signal<string | null>(null);

  constructor() {
    effect(() => {
      const allowed = this.auth.studioAllowed();
      const statusKnown = this.auth.status() !== null;
      if (allowed && !this.studioInitialized) untracked(() => this.initializeStudio());
      else if (statusKnown && !allowed && this.studioInitialized) untracked(() => this.teardownStudio());
    });
    effect(() => {
      if (this.projectHubUrls.appliedRevision() === 0) return;
      untracked(() => this.studioRouteReady.set(true));
    });

    // Cycle 10a: refresh the kanban after a successful create — the
    // CreateTaskFormService doesn't call jobService.refresh itself
    // because that orchestration concern lives here. F2: also flag the
    // new card so it pulses + scrolls into view, and surface a toast
    // with the title that the operator just submitted.
    this.createJobForm.submitted$.subscribe(({ jobId }) => {
      this.refresh();
      this.justCreatedJobId.set(jobId);
      const job = this.jobService.jobs().find((j) => j.id === jobId);
      const title = job?.title ?? jobId;
      this.notifications.success(`Created "${title}"`, 'Task added');
      setTimeout(() => {
        if (this.justCreatedJobId() === jobId) this.justCreatedJobId.set(null);
      }, 2500);
    });

    this.pageContext.createTaskRequests$.subscribe(request => {
      this.createJobForm.openPageTask(request, this.watchPaths());
    });
    this.pageContext.openChatRequests$.subscribe(context => {
      this.orchSideSheetRef?.setActiveProject(context.projectName);
      this.orchSideSheetRef?.show();
    });

    // Cycle 10c: bridge TriageController to the TaskDetailComponent's
    // "acting" highlight via a closure so the ViewChild can resolve
    // lazily at call time. The closure is fine to register here because
    // it doesn't dereference jobDetailRef until invoked.
    this.triage.setClearActingCallback(() => this.jobDetailRef?.clearTriageActing());

    // Keep global board scope in sync with project surfaces. Task and Activity
    // tabs retain the scope that opened them because their project is data-only.
    // setSoleProject is idempotent, so repeated activations do not toggle it off.
    effect(() => {
      if (!this.featureFlags.vsCodeLayout()) return;
      const tab = this.studioTabState.activeTab();
      if (!tab) return;
      // `null`  → workspace-wide ("All projects"): clear the project filter.
      // string  → narrow to exactly that project.
      // `undefined` → detail or context-free tab: leave the current scope
      //               untouched.
      let project: string | null | undefined;
      switch (tab.kind) {
        case 'board':
          project = tab.projectName === '__all__' ? null : tab.projectName;
          break;
        case 'feed':
        case 'chat-history':
          project = null;
          break;
        case 'workbenches':
        case 'workbench':
        case 'hub':
          project = tab.projectName;
          break;
        case 'epics':
          project = tab.projectName;
          break;
        case 'task':
        case 'activity':
          project = undefined;
          break;
        default:
          project = undefined;
      }
      if (project === undefined) return;
      untracked(() => {
        if (project === null) {
          if (!this.boardFilters.hasExplicitProjectFilter()) this.boardFilters.clearProjectScope();
        } else {
          this.boardFilters.setSoleProject(project);
        }
      });
    });

    // Studio-shell mirror: when a job is selected through any path (URL
    // restore, board click, triage advance) and the new shell is on,
    // mirror it as a studio task tab so the editor area can project
    // <app-job-detail> via the task case. Without this the URL-restore
    // path would set selectedJob() but the new shell would show no tab.
    effect(() => {
      const selected = this.selectedJob();
      // Consume the pager/cursor retarget hint up-front (and unconditionally,
      // so a no-op step never leaks the flag into a later genuine open).
      const retargetNav = this.laneNavRetarget;
      this.laneNavRetarget = false;
      if (!this.featureFlags.vsCodeLayout()) return;
      if (!selected) return;
      untracked(() => {
        this.mirrorSelectionToStudioTab(selected, retargetNav);
        if (this.pendingStudioTaskReference) {
          const publicReference = selected.info.key?.trim()
            || selected.info.displayKey?.trim()
            || selected.info.id;
          if (publicReference.toLowerCase() === this.pendingStudioTaskReference.toLowerCase()) {
            this.pendingStudioTaskReference = null;
            this.studioRouteReady.set(true);
          }
        }
      });
    });

    // Browser Back from a key-based task URL to a non-task URL must move the
    // editor shell with it. Selection alone is not tracked by the active-tab
    // effect below, so consume the explicit history event from the routing
    // service and focus the workspace board instead of leaving an empty task
    // tab active under a board URL.
    effect(() => {
      const revision = this.jobSelection.browserRouteCleared();
      if (revision === 0 || !this.featureFlags.vsCodeLayout()) return;
      untracked(() => {
        const tab = this.studioTabState.activeTab();
        if (tab?.kind === 'task' || tab?.kind === 'epic') {
          this.studioTabState.activateAllProjectsBoard();
        }
      });
    });

    // Studio-shell active-tab → selection sync (F5/reload fix). Makes the
    // active studio tab the single source of truth for `selectedJob()` and
    // the canonical `#/tasks/<key>` route, so a reload restores the *current* view:
    //
    //   - Active tab is a task  → ensure `selectedJob` holds that task
    //     (re-hydrating from the persisted tab on a cold reload, so the
    //     task case paints its detail instead of "No task selected"; and
    //     covering in-session selectTab() which only flips the active key).
    //   - Active tab is NOT a task (board / project / hub / diff / activity)
    //     → drop any lingering selection and strip stale task route params.
    //     Without this, switching task→board leaves the task route in the URL and
    //     the next F5 re-opens the task detail instead of the board.
    //
    // Only `activeTab()` is tracked; `selectedJob()` is read untracked so
    // the effect reacts to tab changes (not to the selection updates it and
    // the mirror effect above make), avoiding a feedback loop.
    //
    // The non-task branch strips task route params when we either still hold a selection
    // OR just came from a task tab (`studioActiveTabWasTask`). The latter
    // catches a task→board switch made before the detail fetch resolved:
    // without it the stale param would survive and the in-flight fetch would
    // re-select the task (mid-session replay of the F5 bug). A cold boot that
    // lands on a non-task tab carrying a task deep link is *not* "coming
    // from a task", so the param is preserved for `restoreFromUrl`.
    effect(() => {
      if (!this.featureFlags.vsCodeLayout()) return;
      // A cold shared route resolves asynchronously. Until that route has
      // opened its target tab, the persisted/default active tab is stale
      // hydration input and must not clear the selection that just resolved.
      // The route-in mirror below opens the task tab and releases this gate.
      if (!this.studioRouteReady()) return;
      const tab = this.studioTabState.activeTab();
      untracked(() => {
        const selected = this.selectedJob();
        if (tab?.kind === 'task') {
          this.studioActiveTabWasTask = true;
          if (selected?.info.taskKey === tab.taskKey || this.detailPreview()?.taskKey === tab.taskKey) return;
          this.jobSelection.openDetailByTaskKey(tab.taskKey);
        } else if (tab?.kind === 'epic') {
          this.studioActiveTabWasTask = true;
          this.loadEpicTabDetail(tab.epicKey, tab.viewTaskKey ?? null);
        } else {
          const cameFromTask = this.studioActiveTabWasTask;
          this.studioActiveTabWasTask = false;
          this.epicTabTaskDetail.set(null);
          if (selected || cameFromTask) {
            this.jobSelection.clearSelectionForTabSwitch();
          }
        }
      });
    });

    effect(() => {
      if (!this.featureFlags.vsCodeLayout() || !this.studioRouteReady()) return;
      const tab = this.studioTabState.activeTab();
      if (!tab) return;
      // Project Hub routes are owned by ProjectHubUrlService because their
      // canonical identity is the immutable registry id, not a display slug.
      if (tab.kind === 'hub') return;
      let publicTaskReference: string | null = null;
      if (tab.kind === 'task' || tab.kind === 'epic') {
        const selected = this.selectedJob();
        if (selected) publicTaskReference = selected.info.key?.trim() || selected.info.displayKey?.trim() || selected.info.id;
      }
      const route = studioRouteForTab(tab, publicTaskReference);
      if (!route) return;
      const detailTab = this.routeDetailTab() ?? 'overview';
      const selected = this.selectedJob();
      const inspectorTab = this.routeInspectorTab()
        ?? (selected?.info.state === TaskState.Progress ? 'activity' : 'protocol');
      untracked(() => {
        navigateStudioRoute(route);
        if (tab.kind === 'task') replaceTaskViewRoute(detailTab, inspectorTab);
      });
    });

    // Studio Workspace Settings render as an editor tab. Keep the existing
    // WorkspaceOverlaysService as the section/hash state holder, but close
    // that state when the settings tab is no longer the active surface.
    effect(() => {
      if (!this.featureFlags.vsCodeLayout() || !this.studioRouteReady()) return;
      const tab = this.studioTabState.activeTab();
      const open = this.workspaceOverlays.settingsOpen();
      untracked(() => {
        if (tab?.kind === 'workspace-settings') {
          if (!open) this.workspaceOverlays.open(this.workspaceOverlays.section());
        } else if (open) {
          this.workspaceOverlays.close();
        }
      });
    });

    effect(() => {
      const selected = this.selectedJob();
      const jobs = this.jobService.jobs();

      if (!selected) {
        return;
      }

      const latest = jobs.find((job) => job.taskKey === selected.info.taskKey);
      if (!latest) {
        return;
      }

      const currentExecution = selected.info.execution;
      const latestExecution = latest.execution;
      const executionChanged =
        (currentExecution?.status ?? null) !== (latestExecution?.status ?? null) ||
        (currentExecution?.runOutcome ?? null) !== (latestExecution?.runOutcome ?? null) ||
        (currentExecution?.processId ?? null) !== (latestExecution?.processId ?? null) ||
        (currentExecution?.exitCode ?? null) !== (latestExecution?.exitCode ?? null) ||
        (currentExecution?.durationSeconds ?? null) !== (latestExecution?.durationSeconds ?? null);

      if (selected.info.state === latest.state && !executionChanged) {
        return;
      }

      untracked(() => {
        // Token-guard the re-fetch: if the user (or an auto-advance after a
        // mutation) navigates to a different job while this request is in
        // flight, dropping the late response prevents the panel from
        // snapping back to the prior slug. Without this, a state-change
        // from the detail dropdown races advanceAfterMutation - the shell
        // re-fetches the just-moved job at the same time the pager wants
        // to land on the next slug, and whichever response arrives last
        // wins the `selectedJob` signal.
        const token = this.jobSelection.bumpOpenDetailToken();
        this.jobService.getDetail(latest.id, latest.watchPath).subscribe({
          next: (detail) => this.jobSelection.setSelectedFromAdvance(detail, token),
        });
      });
    });

    // External lane change: when the open job's state diverges from
    // `triageLaneState` and we did NOT initiate the move (no actingId
    // in flight), keep the user on this job but shrink the pager
    // snapshot so Prev/Next navigate the remaining peers.
    effect(() => {
      const sel = this.selectedJob();
      const lane = this.jobSelection.triageLaneState;
      if (!sel || !lane) return;
      if (sel.info.state === lane) return;
      if (this.jobDetailRef?.triageActingId() != null) return;
      untracked(() =>
        this.triage.handleExternalLaneChange(lane, sel.info.taskKey),
      );
    });

  }
  ngOnInit() { this.auth.initialize(); }
  private initializeStudio(): void {
    if (this.studioInitialized) return; this.studioInitialized = true;
    this.orchestratorFeedStore.start();
    // Backlog-lane spec: hydrate the filter bar from the URL hash before
    // rendering so a bookmark or copy-paste lands on the same view.
    this.boardFilters.hydrateFromUrl();
    this.loadTagRegistry();
    // ADR-0046: pre-fetch every CLI's model catalog at boot so the
    // chat-model badge, status-bar picker, and create dialog can render
    // their model lists synchronously instead of paying a round-trip on
    // first open.
    this.cliCatalogStore.hydrateAll();
    // 1-Hz wall-clock tick for the lane status RUNNING pill's elapsed
    // string. Light enough to leave running without gating - the only
    // consumer is the lane column's statusCluster computed.
    this.nowMsTickHandle = setInterval(() => this.nowMs.set(Date.now()), 1000);
    this.refresh();
    this.jobService.startLiveUpdates();
    this.projectHubUrls.start();
    this.loadWatchPaths();
    this.jobService.refreshRunnerStatus();
    this.devTools.loadFlags();
    this.clientService.refresh();
    this.jobSelection.restoreFromUrl();

    // Deep-link: open the workspace token timeline when the URL already
    // points at it, and keep the overlay in sync as the hash changes.
    // Also reconciles legacy top-level hash routes into their current
    // editor-tab destinations.
    const applyHash = () => {
      this.workspaceOverlays.syncFromHash();
      if (this.featureFlags.vsCodeLayout() && this.workspaceOverlays.settingsOpen()) {
        this.openWorkspaceSettingsInStudio(this.workspaceOverlays.section());
      }
      const studioHandled = this.syncStudioRouteFromHash();
      if (!studioHandled) this.applyProjectShellHash();
      this.syncEpicsTabFromHash();
    };
    applyHash();
    this.hashListener = applyHash;
    window.addEventListener('hashchange', this.hashListener);
    window.addEventListener('popstate', this.hashListener);

    // Keyboard shortcuts for kanban container focus-expand: 1/2/3 focus
    // the corresponding container, 0 exits focus. Suppressed while the
    // user is typing in an input/textarea/contenteditable and while a
    // detail/overlay is open (the kanban isn't visible then).
    this.kanbanKeyListener = (ev: KeyboardEvent) => {
      if (ev.defaultPrevented || ev.metaKey || ev.ctrlKey || ev.altKey) return;
      if (this.selectedJob() !== null) return;
      if (this.showCreate()) return;
      if (this.workspaceOverlays.settingsOpen()) return;
      if (this.projectShellName() !== null) return;
      if (this.studioTabState.activeTab()?.kind === 'epics') return;
      const target = ev.target as HTMLElement | null;
      if (target) {
        const tag = target.tagName;
        if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') return;
        if ((target as HTMLElement).isContentEditable) return;
      }
      const ids = this.laneGroups().map((g) => g.id);
      if (ev.key === '1' && ids[0]) {
        this.toggleContainerFocus(ids[0]);
        ev.preventDefault();
        return;
      }
      if (ev.key === '2' && ids[1]) {
        this.toggleContainerFocus(ids[1]);
        ev.preventDefault();
        return;
      }
      if (ev.key === '3' && ids[2]) {
        this.toggleContainerFocus(ids[2]);
        ev.preventDefault();
        return;
      }
      if (ev.key === '0') {
        this.clearContainerFocus();
        ev.preventDefault();
        return;
      }
      // F25: `/` opens the activity-bar Filters panel and focuses its
      // search input. Replaces the previous binding that toggled the
      // right-edge filter sidesheet.
      if (ev.key === '/' && this.featureFlags.vsCodeLayout()) {
        this.openFiltersPanelAndFocusSearch();
        ev.preventDefault();
        return;
      }
    };
    window.addEventListener('keydown', this.kanbanKeyListener);

  }

  private teardownStudio(): void {
    this.jobService.stopLiveUpdates();
    this.orchestratorFeedStore.stop();
    if (this.hashListener) {
      window.removeEventListener('hashchange', this.hashListener);
      window.removeEventListener('popstate', this.hashListener);
      this.hashListener = null;
    }
    if (this.kanbanKeyListener) {
      window.removeEventListener('keydown', this.kanbanKeyListener);
      this.kanbanKeyListener = null;
    }
    if (this.nowMsTickHandle !== null) {
      clearInterval(this.nowMsTickHandle);
      this.nowMsTickHandle = null;
    }
    this.studioInitialized = false;
    this.projectHubUrls.stop();
  }

  ngOnDestroy() { this.teardownStudio(); }

  onE2EDidDelete(): void {
    this.jobService.refresh(true);
  }

  refresh() {
    this.jobService.refresh();
  }

  openDetail(job: TaskInfo) {
    if (job.kind === 'epic') {
      this.openEpicAsTab(job);
      return;
    }
    this.routeDetailTab.set(null);
    this.routeInspectorTab.set(null);
    if (this.featureFlags.vsCodeLayout()) {
      this.studioTabState.open({ kind: 'task', taskKey: job.taskKey });
    }
    this.jobSelection.openDetailAfterPaint(job);
  }
  closeDetail() {
    this.jobSelection.closeDetail();
  }
  retryDetailLoad() {
    this.jobSelection.retryDetailLoad();
  }
  isSelectedJob(job: TaskInfo): boolean {
    return this.jobSelection.isSelected(job);
  }

  // Cycle 10c: triage panel + j/k navigation + auto-advance delegated
  // --- studio slim tab-bar triage cluster ---------------------------------
  // Mirror of the kanban detail-header's primary + overflow cluster, anchored
  // in the studio shell's slim tab-bar header (the VS Code layout hides
  // <app-detail-header>, so the cluster needs its own seat there). All
  // actions route through the same TriageController paths as the kanban
  // panel; this is purely a second render site.
  readonly studioTriagePrimary = computed<TriageButton | null>(() => {
    const sel = this.selectedJob();
    return sel ? primaryActionFor(sel.info.state) : null;
  });
  readonly studioTriageOverflow = computed<TriageButton[]>(() => {
    const sel = this.selectedJob();
    return sel ? overflowActionsFor(sel.info.state) : [];
  });
  /**
   * State-dependent presentation for the studio Human Review acceptance primary
   * (`mark-done`). Null for every other primary. When the work has already
   * landed it carries the landed-status pill text and relabels "Merge into
   * Develop" to "Accept"; the computed `integration` field is the only
   * target-membership proof. A live released-to-main hint can refine the wording.
   */
  readonly studioMergeAcceptView = computed<MergeAcceptView | null>(() => {
    const sel = this.selectedJob();
    const p = this.studioTriagePrimary();
    if (!sel || !p || p.id !== 'mark-done') return null;
    return mergeAcceptViewFor(sel.info, this.jobDetailSig()?.landedState() ?? null);
  });
  /** Effective studio primary label (state-aware for the Human Review acceptance). */
  readonly studioPrimaryLabel = computed(() => {
    const p = this.studioTriagePrimary();
    if (!p) return '';
    return this.studioMergeAcceptView()?.acceptLabel ?? p.label;
  });
  /**
   * True while the studio Human Review acceptance primary (`mark-done`) depends
   * on git status that has not loaded yet. Until the graph-derived provenance
   * settles, `landedState` is `null` and the label would guess "Merge into
   * Develop" (an actionable merge) before flipping to "Accept" once the work is
   * revealed as already merged - the race Robert reported (AGT-2006). The button
   * stays disabled + skeletoned while this holds, then switches atomically.
   */
  readonly studioPrimaryAwaitingGit = computed(() => {
    const p = this.studioTriagePrimary();
    if (!p || p.id !== 'mark-done') return false;
    return this.jobDetailSig()?.gitInfoLoading() ?? false;
  });
  readonly studioTriageHasActions = computed(
    () => this.studioTriagePrimary() !== null || this.studioTriageOverflow().length > 0 || this.studioCommitActionsAvailable(),
  );
  readonly studioTriageOverflowOpen = signal(false);
  readonly studioTriageOverflowAnchor = signal<HTMLElement | null>(null);
  studioCommitActionsAvailable(): boolean {
    return this.selectedJob() !== null && !!this.jobDetailRef?.commitActionsAvailable();
  }
  studioTriageMenuItems(): MenuItem[] {
    const blocked = this.updateClient.mutationsBlocked();
    const items = this.studioTriageOverflow().map<MenuItem>(b => ({
      kind: 'row',
      id: b.id,
      label: b.label,
      danger: b.variant === 'danger',
      disabled: blocked,
    }));
    if (this.studioCommitActionsAvailable()) {
      if (items.length > 0) items.push({ kind: 'separator' });
      items.push(
        {
          kind: 'row',
          id: 'generate-commit-message',
          label: this.jobDetailRef?.generatingMsg() ? 'Generating Commit Message...' : 'Generate Commit Message',
          disabled: blocked || !!this.jobDetailRef?.generatingMsg() || !!this.jobDetailRef?.committing(),
        },
        {
          kind: 'row',
          id: 'add-commit',
          label: this.jobDetailRef?.committing() ? 'Committing...' : 'Add Commit...',
          hint: this.jobDetailRef?.commitMessage().trim() ? 'Draft ready' : undefined,
          disabled: blocked || !!this.jobDetailRef?.generatingMsg() || !!this.jobDetailRef?.committing(),
        },
      );
    }
    return items;
  }

  onStudioTriagePrimary(): void {
    const sel = this.selectedJob();
    const p = this.studioTriagePrimary();
    if (!sel || !p) return;
    // Hold git-dependent acceptance until the branch/merge status has loaded, so
    // a click cannot trigger a merge while the label is still a guess (AGT-2006).
    if (this.studioPrimaryAwaitingGit()) return;
    this.dispatchStudioTriage(sel.info, p);
  }

  toggleStudioTriageOverflow(event: MouseEvent): void {
    event.stopPropagation();
    if (this.updateClient.mutationsBlocked()) return;
    this.studioTriageOverflowAnchor.set(event.currentTarget as HTMLElement);
    this.studioTriageOverflowOpen.update(v => !v);
  }
  closeStudioTriageOverflow(): void {
    this.studioTriageOverflowOpen.set(false);
  }

  onStudioTriageMenuItemClick(ev: MenuItemClickEvent): void {
    if (ev.id === 'generate-commit-message') {
      this.studioTriageOverflowOpen.set(false);
      this.jobDetailRef?.generateCommitMessage();
      return;
    }
    if (ev.id === 'add-commit') {
      this.studioTriageOverflowOpen.set(false);
      this.jobDetailRef?.addCommitFromMenu();
      return;
    }
    const button = this.studioTriageOverflow().find(b => b.id === ev.id);
    const sel = this.selectedJob();
    if (!sel || !button) return;
    this.studioTriageOverflowOpen.set(false);
    if (button.id === 'delete') {
      this.onDeleteFromDetail(sel.info);
      return;
    }
    this.dispatchStudioTriage(sel.info, button);
  }

  private dispatchStudioTriage(info: TaskInfo, button: TriageButton): void {
    const id = button.id;
    switch (button.intent.kind) {
      case 'move':
        this.onTriageMove(info, { targetState: button.intent.targetState, actionId: id });
        return;
      case 'moveToTop':
        this.onTriageMoveToTop(info, { actionId: id });
        return;
      case 'delete':
        this.onTriageDelete(info, { actionId: id });
        return;
      case 'start':
        this.onTriageStart(info, { actionId: id });
        return;
      case 'stop':
      case 'editPrompt':
      case 'showActivity':
        // Pane-local intents (stop a running job, jump to the prompt editor,
        // switch the inspector to activity) only make sense inside the
        // detail panel. The kanban detail-header still surfaces them; the
        // studio slim header skips them to keep the row short.
        return;
    }
  }

  // to TriageController. The shell forwards events from TaskDetailComponent.
  onTriageMove(info: TaskInfo, ev: { targetState: string; actionId: string }) {
    this.triage.move(info, ev);
  }
  onTriageMoveToTop(info: TaskInfo, ev: { actionId: string }) {
    this.triage.moveToTop(info, ev);
  }
  onTriageDelete(info: TaskInfo, ev: { actionId: string }) {
    this.triage.delete(info, ev);
  }
  onTriageStart(info: TaskInfo, ev: { actionId: string }) {
    this.triage.start(info, ev);
  }
  onTriageNext(info: TaskInfo) {
    // Pager / cursor navigation reuses the current task tab (see the
    // studio-shell mirror effect) instead of stacking a fresh tab per step.
    if (this.triage.next(info)) this.laneNavRetarget = true;
  }
  onTriagePrev(info: TaskInfo) {
    if (this.triage.prev(info)) this.laneNavRetarget = true;
  }

  // Cycle 10b: board-mutation handlers delegate to BoardMutationsService.
  onJobDrop(event: { jobId: string; watchPath: string; targetState: string; targetIndex: number }) {
    this.boardMutations.moveJob(event);
  }
  onJobReorder(event: { state: string; jobs: { jobId: string; watchPath: string }[] }) {
    this.boardMutations.reorderJobs(event);
  }
  onDeleteFromBoard(job: TaskInfo) {
    this.boardMutations.deleteFromBoard(job);
  }

  /**
   * F5: bubble-up from <app-job-card>'s "Pick next" affordance.
   * Promotes the card to position 1 in the runner queue. Re-uses the
   * existing `moveJobToTop` round-trip — same endpoint the detail-view
   * "Do Next" already drove. Toasts on success so the operator gets a
   * clear "the runner now sees this first" moment.
   */
  onPickNext(job: TaskInfo) {
    this.jobService.moveJobToTop(job.id, job.watchPath).subscribe({
      next: () => {
        this.notifications.success(
          `"${job.title || job.id}" is next up`,
          'Pick next',
        );
        this.refresh();
      },
      error: (err) => {
        this.errorDialog.show(err, {
          title: 'Failed to bump task',
          fallbackMessage: 'Failed to bump task to the front of the queue',
          source: `Task ${job.id}`,
        });
      },
    });
  }
  onDeleteFromDetail(info: TaskInfo) {
    this.boardMutations.deleteFromDetail(info);
  }
  /** Lane dropdown navigation forwarded from the kanban detail view
   *  (navigation-only; the open task is never moved here). */
  onNavigateLane(lane: string) {
    this.jobSelection.navigateToLane(lane);
  }
  onArchiveAll() {
    this.boardMutations.archiveAllCompleted(this.filteredGrouped().completed);
  }

  /**
   * Epic overview screen "open epic" / "open sub-task" click. The Epics
   * overview is a normal editor tab, so navigation opens an inline epic
   * detail tab and keeps the overview tab in the tab strip.
   */
  onEpicOverviewOpenTask(event: { jobId: string; watchPath: string }): void {
    const requestToken = ++this.relatedOpenToken;
    this.jobService.getDetail(event.jobId, event.watchPath).subscribe({
      next: (detail) => {
        if (requestToken !== this.relatedOpenToken) return;
        if (detail.info.kind === 'epic') {
          this.openEpicDetailAsTab(detail);
          return;
        }
        const epic = this.jobService.jobs().find((job) =>
          job.kind === 'epic' &&
          job.id === detail.info.epicId &&
          job.watchPath === detail.info.watchPath
        );
        if (!epic) {
          this.selectFetchedDetail(detail);
          return;
        }
        this.openEpicAsTab(epic, detail.info.taskKey);
        this.selectEpicTabSubTask(detail);
      },
      error: (err) => {
        if (requestToken !== this.relatedOpenToken) return;
        this.errorDialog.show(err, {
          title: 'Failed to open task',
          source: `task ${event.jobId}`,
        });
      },
    });
  }

  closeStudioTab(tab: StudioTab): void {
    this.studioTabState.close(studioTabKey(tab));
  }

  /**
   * Load (or reload) the backend WatchPaths into `watchPaths`. Runs once at
   * boot and again whenever a project is deleted from the studio shell — the
   * deleted project's WatchPaths entry is gone server-side, so re-pulling here
   * drops its name from `projectNames()` (no ghost picker / tree row) and the
   * job refresh clears its now-empty board. Also re-purges stale board filters
   * and persisted tabs against the surviving name set.
   */
  loadWatchPaths(): void {
    this.jobService.getWatchPaths().subscribe({
      next: (entries) => {
        this.watchPaths.set(entries);
        if (entries.length > 0) this.createJobForm.newWatchPath = entries[0].path;

        // Purge stale project names that survived a registry rename / delete in
        // localStorage (board filter) and persisted tabs.
        const validNames = new Set(entries.map(e => e.name));
        this.boardFilters.purgeStaleProjects(validNames);
        this.studioTabState.purgeStaleProjectTabs(validNames);

        // The deep-link hash listener can fire before watch paths are
        // known (e.g. on a hard reload of `#/projects/<slug>`); resolving
        // the slug → project name needs the watch-path list, so re-apply
        // once entries are available.
        const studioHandled = this.syncStudioRouteFromHash();
        if (!studioHandled) this.applyProjectShellHash();

        // Refresh the board so a deleted project's jobs disappear right away
        // rather than on the next live-update tick.
        this.jobService.refresh(true);
      },
      error: (err) => {
        this.errorDialog.show(err, {
          title: 'Failed to load projects',
          fallbackMessage: 'Failed to load projects',
          source: 'Project list',
        });
      },
    });
  }
  openCreate(targetState?: string) {
    if (this.updateClient.mutationsBlocked()) return;
    this.createJobForm.open({
      watchPaths: this.watchPaths(),
      activeProjects: this.activeProjects(),
      targetState,
    });
  }
  onSearchInput(event: Event) {
    const value = (event.target as HTMLInputElement).value;
    this.boardFilters.setSearchQuery(value);
  }

  setSearchQuery(value: string) {
    this.boardFilters.setSearchQuery(value);
  }

  clearSearch() {
    this.boardFilters.clearSearch();
  }

  /**
   * Toggle the orchestrator feed overlay. Picks the project to show by
   * preferring (1) the currently open detail's project, (2) the first
   * active project filter, (3) the first known watch path. Closes the
   * overlay if it is already open.
   */
  toggleOrchFeed(): void {
    if (this.orchFeedProject() !== null) {
      this.projectOverlays.closeOrchFeed();
      return;
    }
    const project = this.pickOrchFeedProject();
    if (!project) return;
    this.projectOverlays.openOrchFeed(project);
  }

  closeOrchFeed(): void {
    this.orchFeedProject.set(null);
  }

  /** Tooltip for the toolbar button; shows which project the feed will open for. */
  orchFeedTooltip(): string {
    const project = this.pickOrchFeedProject();
    return project ? `Open orchestrator feed for "${project}"` : 'No project selected';
  }

  /**
   * Project the orchestrator side sheet should align to. Tracks the
   * currently open detail's project so flipping into a task and then
   * opening the side sheet picks the right thread automatically. When
   * no detail is open, falls back to the first active or first known
   * project the same way the feed overlay does.
   */
  readonly orchSideSheetPreferredProject = computed<string | null>(() => {
    const page = this.pageContext.activePage();
    if (page?.projectName) return page.projectName;
    const detail = this.selectedJob();
    if (detail?.info?.projectName) return detail.info.projectName;
    const active = [...this.activeProjects()];
    if (active.length > 0) return active[0];
    const watchPaths = this.watchPaths();
    return watchPaths.length > 0 ? watchPaths[0].name : null;
  });

  /**
   * Project the active Epics tab is scoped to. Workspace-wide Epics tabs pass
   * null, which keeps the overview read-only across projects.
   */
  readonly activeEpicScopedProject = computed<EpicOverviewScope | null>(() => {
    const tab = this.studioTabState.activeTab();
    if (tab?.kind !== 'epics' || !tab.projectName) return null;
    const name = tab.projectName;
    const entry = this.watchPaths().find((wp) => wp.name === name);
    if (!entry) return null;
    return { name, watchPath: entry.path };
  });

  /**
   * Live CLI run in scope for the orchestrator side sheet's "where am I"
   * header. When a task detail is open we report that task's own run;
   * otherwise (board scope) we surface the running task in the preferred
   * project so the operator sees "a run is live" the moment they open the
   * orchestrator on a busy project. Null when nothing is executing in scope.
   */
  readonly orchSideSheetActiveRun = computed<{ model: string | null; startedAt: string | null } | null>(() => {
    const detail = this.selectedJob();
    if (detail?.info) {
      const exec = detail.info.execution;
      return exec && exec.status === 'running'
        ? { model: exec.model, startedAt: exec.startedAt }
        : null;
    }
    const project = this.orchSideSheetPreferredProject();
    if (!project) return null;
    const running = this.jobService
      .jobs()
      .find((j) => j.projectName === project && j.state === TaskState.Progress && j.execution?.status === 'running');
    const exec = running?.execution;
    return exec ? { model: exec.model, startedAt: exec.startedAt } : null;
  });

  orchChatTooltip(): string {
    const project = this.orchSideSheetPreferredProject();
    return project ? `Toggle orchestrator chat for "${project}"` : 'No project selected';
  }

  toggleOrchestratorChat(): void {
    this.orchSideSheetRef?.toggle();
  }

  openChatHistoryContext(contextKey: string): void {
    this.orchSideSheetRef?.openManagedContext(contextKey);
  }

  onNavigateToChatContext(contextKey: string): void {
    if (contextKey === 'global') {
      this.studioTabState.activateAllProjectsBoard();
      return;
    }
    if (contextKey.startsWith('project:')) {
      this.studioTabState.open({ kind: 'board', projectName: contextKey.slice('project:'.length) });
      return;
    }
    if (!contextKey.startsWith('task:')) return;
    const slash = contextKey.indexOf('/');
    if (slash < 0) return;
    const projectName = contextKey.slice('task:'.length, slash);
    const taskKey = contextKey.slice(slash + 1);
    const task = this.jobService.jobs().find(item => item.projectName === projectName
      && (item.taskKey === taskKey || item.displayKey === taskKey || item.key === taskKey));
    if (task) this.studioTabState.open({ kind: 'task', taskKey: task.taskKey });
  }

  /**
   * Phase: Verbose Debug. The orchestrator side sheet's "🐞" header button
   * fires this with the active task's id + watch path. We fetch the
   * evidence (cli output, run timeline, screenshots, plus the latest job
   * detail for token summary) in parallel and feed it to the shared
   * `<app-verbose-debug-overlay>`. The overlay is read-only; it never
   * mutates state and never starts a run.
   */
  onOpenVerboseDebugFromSheet(event: {
    jobId: string;
    watchPath: string;
    jobTitle: string | null;
  }): void {
    forkJoin({
      detail: this.jobService.getDetail(event.jobId, event.watchPath),
      lines: this.jobService.getJobOutput(event.jobId, event.watchPath),
      runs: this.jobService.getRunTimeline(event.jobId, event.watchPath),
      screenshots: this.jobService.getJobScreenshots(event.jobId, event.watchPath),
    }).subscribe({
      next: ({ detail, lines, runs, screenshots }) => {
        this.verboseDebugContext.set({
          lines: lines ?? [],
          runTimeline: runs ?? null,
          screenshots: screenshots?.screenshots ?? [],
          tokenSummary: detail?.info?.tokenSummary ?? null,
          job: detail?.info ?? null,
        });
      },
      error: (err) => {
        this.errorDialog.show(err, {
          title: 'Verbose Debug failed to load',
          source: `task ${event.jobTitle ?? event.jobId}`,
        });
      },
    });
  }

  closeVerboseDebug(): void {
    this.verboseDebugContext.set(null);
  }

  private selectFetchedDetail(detail: TaskDetail): void {
    this.jobSelection.selectResolvedDetail(detail, 'push');
  }

  private openEpicAsTab(job: TaskInfo, viewTaskKey?: string): void {
    this.studioTabState.open({ kind: 'epic', epicKey: job.taskKey, viewTaskKey });
    this.jobService.getDetail(job.id, job.watchPath).subscribe({
      next: (detail) => {
        if (detail.info.kind === 'epic') {
          const token = this.jobSelection.bumpOpenDetailToken();
          this.jobSelection.setSelectedFromAdvance(detail, token);
        }
      },
      error: (err) => this.errorDialog.show(err, { title: 'Failed to open epic', source: `task ${job.id}` }),
    });
  }

  private openEpicDetailAsTab(detail: TaskDetail, viewTaskKey?: string): void {
    this.epicTabTaskDetail.set(null);
    this.studioTabState.open({ kind: 'epic', epicKey: detail.info.taskKey, viewTaskKey });
    const token = this.jobSelection.bumpOpenDetailToken();
    this.jobSelection.setSelectedFromAdvance(detail, token);
  }

  private loadEpicTabDetail(epicKey: string, viewTaskKey: string | null): void {
    if (this.selectedJob()?.info.taskKey !== epicKey) {
      this.jobSelection.openDetailByTaskKey(epicKey);
    }
    if (viewTaskKey) {
      this.fetchDetailByTaskKey(viewTaskKey, (detail) => this.selectEpicTabSubTask(detail));
    } else {
      this.epicTabTaskDetail.set(null);
    }
  }

  private fetchDetailByTaskKey(taskKey: string, onDetail: (detail: TaskDetail) => void): void {
    const sep = taskKey.lastIndexOf('::');
    if (sep < 0) return;
    const watchPath = taskKey.slice(0, sep);
    const jobId = taskKey.slice(sep + 2);
    if (!jobId || !watchPath) return;
    this.jobService.getDetail(jobId, watchPath).subscribe({
      next: onDetail,
      error: (err) => this.errorDialog.show(err, { title: 'Failed to open task', source: `task ${jobId}` }),
    });
  }

  /** Target-aware open helper. Epic targets become inline epic tabs. */
  openRelatedJob(event: { jobId: string; watchPath: string }): void {
    const requestToken = ++this.relatedOpenToken;
    this.jobService.getDetail(event.jobId, event.watchPath).subscribe({
      next: (detail) => {
        if (requestToken !== this.relatedOpenToken) return;
        if (detail.info.kind === 'epic') {
          this.openEpicDetailAsTab(detail);
          return;
        }
        this.selectFetchedDetail(detail);
      },
      error: (err) => {
        if (requestToken !== this.relatedOpenToken) return;
        this.errorDialog.show(err, {
          title: 'Failed to open task',
          source: `task ${event.jobId}`,
        });
      },
    });
  }

  /**
   * Slice E: route a click on the bug-confirmation card's "Open task"
   * action to the kanban detail panel. Epic targets open as inline epic
   * tabs instead of modal overlays.
   */
  onOpenJobDetailFromSheet(event: { jobId: string; watchPath: string }): void {
    this.openRelatedJob(event);
  }

  onOpenEpicFromTaskAnchor(currentTask: TaskInfo, event: { jobId: string; watchPath: string }): void {
    const requestToken = ++this.relatedOpenToken;
    this.jobService.getDetailByProject(
      event.jobId,
      currentTask.projectName,
      event.watchPath,
    ).subscribe({
      next: (detail) => {
        if (requestToken !== this.relatedOpenToken) return;
        if (detail.info.kind !== 'epic') {
          this.errorDialog.show(
            new Error(`Task ${event.jobId} is not an epic.`),
            {
              title: 'Failed to open epic',
              fallbackMessage: 'The selected parent is not an epic.',
              source: `task ${event.jobId}`,
            },
          );
          return;
        }
        this.openEpicDetailFromTaskAnchor(detail);
      },
      error: (err) => {
        if (requestToken !== this.relatedOpenToken) return;
        this.errorDialog.show(err, {
          title: 'Failed to open epic',
          source: `task ${event.jobId}`,
        });
      },
    });
  }

  /**
   * Map the current `selectedJob` onto a studio editor tab (vsCodeLayout):
   * focus the existing tab when present, otherwise open a fresh one — except
   * for a pager / cursor step (`retargetNav`), which reuses the active task
   * tab in place so walking a lane reloads one tab instead of stacking a
   * trail of them. Extracted from the mirror effect so the open-vs-retarget
   * decision is unit-testable without driving the full app lifecycle.
   */
  private mirrorSelectionToStudioTab(selected: TaskDetail, retargetNav: boolean): void {
    if (selected.info.kind === 'epic') {
      const key = `epic:${selected.info.taskKey}`;
      const present = this.studioTabState.tabs().some(
        (t) => t.kind === 'epic' && t.epicKey === selected.info.taskKey,
      );
      if (!present) {
        this.studioTabState.open({ kind: 'epic', epicKey: selected.info.taskKey });
      } else {
        this.studioTabState.select(key);
      }
      return;
    }
    const key = `task:${selected.info.taskKey}`;
    const present = this.studioTabState.tabs().some(
      (t) => t.kind === 'task' && t.taskKey === selected.info.taskKey,
    );
    if (present) {
      // Already open elsewhere → just focus it (never duplicate).
      this.studioTabState.select(key);
      return;
    }
    const active = this.studioTabState.activeTab();
    if (retargetNav && active?.kind === 'task') {
      // Pager / cursor step from one task to the next: reuse the tab we
      // navigated away from instead of opening a new one.
      this.studioTabState.retarget(studioTabKey(active), { kind: 'task', taskKey: selected.info.taskKey });
    } else {
      this.studioTabState.open({ kind: 'task', taskKey: selected.info.taskKey });
    }
  }

  private openEpicDetailFromTaskAnchor(detail: TaskDetail): void {
    this.epicTabTaskDetail.set(null);
    const target = { kind: 'epic' as const, epicKey: detail.info.taskKey };
    this.studioTabState.open(target);

    const token = this.jobSelection.bumpOpenDetailToken();
    this.jobSelection.setSelectedFromAdvance(detail, token);
  }

  onEpicTabOpenSubTask(event: { jobId: string; watchPath: string }): void {
    const tab = this.studioTabState.activeTab();
    const epic = this.selectedJob();
    if (tab?.kind !== 'epic') return;
    if (!epic) return;
    const requestToken = ++this.relatedOpenToken;
    this.jobService.getDetail(event.jobId, event.watchPath).subscribe({
      next: (detail) => {
        if (requestToken !== this.relatedOpenToken) return;
        if (
          detail.info.kind === 'epic' ||
          detail.info.epicId !== epic.info.id ||
          detail.info.watchPath !== epic.info.watchPath
        ) {
          this.errorDialog.show(new Error('The selected task is not part of this epic.'), {
            title: 'Failed to open sub-task',
            source: `task ${event.jobId}`,
          });
          return;
        }
        if (tab.viewTaskKey) {
          const taskKey = `task:${detail.info.taskKey}`;
          if (this.studioTabState.tabs().some(t => studioTabKey(t) === taskKey)) {
            this.studioTabState.select(taskKey);
            return;
          }
          this.studioTabState.retarget(studioTabKey(tab), {
            kind: 'epic',
            epicKey: tab.epicKey,
            viewTaskKey: detail.info.taskKey,
          });
        }
        this.selectEpicTabSubTask(detail);
      },
      error: (err) => {
        if (requestToken !== this.relatedOpenToken) return;
        this.errorDialog.show(err, {
          title: 'Failed to open sub-task',
          source: `task ${event.jobId}`,
        });
      },
    });
  }

  private selectEpicTabSubTask(detail: TaskDetail): void {
    this.epicTabTaskDetail.set(detail);
    this.captureEpicTabPager(detail);
  }

  private captureEpicTabPager(detail: TaskDetail): void {
    const peers = this.epicTabSubTaskPeers();
    if (peers.length === 0) return;
    this.lanePager.capture('epic', peers, detail.info.taskKey);
  }

  closeEpicTabTaskDetail(): void {
    this.epicTabTaskDetail.set(null);
  }

  onEpicTabNavigateLane(targetState: string): void {
    const next = this.epicTabSubTaskPeers().find((job) => job.state === targetState);
    if (!next) return;
    this.onEpicTabOpenSubTask({ jobId: next.id, watchPath: next.watchPath });
  }

  onEpicTabNextSubTask(info: TaskInfo): void {
    this.openAdjacentEpicTabSubTask(info, 1);
  }

  onEpicTabPrevSubTask(info: TaskInfo): void {
    this.openAdjacentEpicTabSubTask(info, -1);
  }

  private openAdjacentEpicTabSubTask(info: TaskInfo, delta: -1 | 1): void {
    const peers = this.epicTabSubTaskPeers();
    if (peers.length === 0) return;
    const idx = peers.findIndex((job) => job.taskKey === info.taskKey);
    const nextIdx = idx < 0 ? 0 : idx + delta;
    if (nextIdx < 0 || nextIdx >= peers.length || nextIdx === idx) return;
    const next = peers[nextIdx];
    this.onEpicTabOpenSubTask({ jobId: next.id, watchPath: next.watchPath });
  }

  /**
   * Project Security panel "Create follow-up task" action (slice 1 of the
   * quality-system mockup). Opens the existing create-job dialog
   * pre-filled with the prompt body the panel composed from the most
   * recent review. The user picks model / target-state / title overrides
   * before submitting; the panel never queues a job behind the user's back.
   */
  onSecurityFollowUp(event: { projectName: string; prefill: string }): void {
    this.createJobForm.openSecurityFollowUp(event, this.watchPaths());
  }

  /**
   * "Open evidence" action: refresh the kanban so the freshly written
   * review is visible at the top of the history list. Slice 1 keeps this
   * deliberately minimal - a true file viewer overlay belongs in a later
   * slice. Today the project's `security/reviews/` folder is the canonical
   * pointer; the panel already shows the rel path next to each row.
   */
  onSecurityOpenEvidence(event: { projectName: string; relPath: string }): void {
    void event;
    // The relPath is rendered in the panel row itself; refresh the kanban
    // so a freshly-queued audit's eventual completion is visible without
    // a manual reload.
    this.refresh();
  }

  /** Refresh the kanban after a security audit was queued so the new job appears. */
  onSecurityAuditQueued(event: { projectName: string; jobId: string }): void {
    void event;
    this.refresh();
  }

  /**
   * Project UX/UI panel "Create follow-up task" / per-row "Task" action
   * (slice 6 of the quality-system mockup). Opens the existing create-job
   * dialog pre-filled with a prompt body the panel composed from the
   * council note or the design overview. The user picks model /
   * target-state / title before submitting.
   */
  onUxuiFollowUp(event: { projectName: string; prefill: string; title: string }): void {
    this.createJobForm.openUxuiFollowUp(event, this.watchPaths());
  }

  /** Refresh the kanban after a UX/UI design action was queued so the new job appears. */
  onUxuiActionQueued(event: { projectName: string; action: string; jobId: string }): void {
    void event;
    this.refresh();
  }

  onOpenWorkbenchWiki(projectName: string, relPath: string): void {
    if (!this.projectHubUrls.openWikiPage(projectName, relPath)) {
      this.notifications.error(`Could not open ${relPath} in the project Wiki.`, 'Wiki navigation');
    }
  }

  onWorkbenchOverviewOpen(event: { projectName: string; workbench: { id: string; title: string; key?: string | null } }): void {
    const { projectName, workbench } = event;
    this.studioTabState.open({ kind: 'workbench', projectName, workbenchId: workbench.id,
      title: workbench.title, key: workbench.key ?? undefined });
  }

  private pickOrchFeedProject(): string | null {
    const detail = this.selectedJob();
    if (detail?.info?.projectName) return detail.info.projectName;
    const active = [...this.activeProjects()];
    if (active.length > 0) return active[0];
    const watchPaths = this.watchPaths();
    return watchPaths.length > 0 ? watchPaths[0].name : null;
  }

  // Cycle 9g: project-overlay open/close + URL-hash sync delegated to
  // ProjectOverlaysService. The shell keeps thin pass-through methods
  // because external entry points (project-tabs, kanban project chip)
  // still go through it.
  openProjectShell(name: string, rail: ProjectRailKey = DEFAULT_PROJECT_RAIL_KEY): void {
    this.projectOverlays.openProjectShell(name, rail, this.watchPaths());
  }
  closeProjectShell(): void {
    this.projectOverlays.closeProjectShell();
  }
  openAnalysisReport(project: string, reportId: string): void {
    this.projectOverlays.openAnalysisReport(project, reportId);
  }
  closeAnalysisReport(): void {
    this.projectOverlays.closeAnalysisReport();
  }
  private applyProjectShellHash(fromHistory = false): void {
    if (this.featureFlags.vsCodeLayout()) {
      this.projectHubUrls.applyHash(fromHistory);
    } else {
      this.projectOverlays.syncShellFromHash(this.watchPaths());
    }
    this.projectOverlays.syncFeedFromHash(this.watchPaths());
  }

  private syncStudioRouteFromHash(): boolean {
    if (!this.featureFlags.vsCodeLayout()) return false;
    const route = parseStudioRoute(window.location.hash);
    if (!route) {
      const legacyTask = new URLSearchParams(window.location.search).get('task')
        || new URLSearchParams(window.location.search).get('job');
      if (legacyTask) {
        this.pendingStudioTaskReference = legacyTask;
        return true;
      }
      this.studioRouteReady.set(true);
      return false;
    }
    this.studioRouteReady.set(false);

    if (route.kind === 'hub') {
      if (this.projectHubUrls.applyHash(false)) {
        this.studioRouteReady.set(true);
      }
      return true;
    }

    const projectName = 'projectSlug' in route && route.projectSlug
      ? this.watchPaths().find(entry => studioProjectSlug(entry.name) === route.projectSlug)?.name ?? null : null;
    if ('projectSlug' in route && route.projectSlug && !projectName) {
      return true;
    }

    switch (route.kind) {
      case 'board':
        this.studioTabState.open({ kind: 'board', projectName: projectName ?? '__all__' });
        break;
      case 'feed':
        this.studioTabState.open({ kind: 'feed' });
        break;
      case 'chat-history':
        this.studioTabState.open({ kind: 'chat-history' });
        break;
      case 'workbench':
        this.studioTabState.open({ kind: 'workbench', projectName: projectName!, workbenchId: route.workbenchId });
        break;
      case 'workbenches':
        this.studioTabState.open({ kind: 'workbenches', projectName });
        break;
      case 'task':
        this.routeDetailTab.set(route.tab);
        this.routeInspectorTab.set(route.inspector);
        this.pendingStudioTaskReference = route.reference;
        this.jobSelection.restoreFromUrl();
        return true;
      case 'epics':
        this.studioTabState.open({ kind: 'epics', projectName });
        break;
      case 'epic':
        this.pendingStudioTaskReference = route.reference;
        this.jobSelection.restoreFromUrl();
        return true;
      case 'workspace-settings':
        if (!this.workspaceOverlays.settingsOpen()) {
          this.studioRouteReady.set(true);
          return false;
        }
        this.studioTabState.open({ kind: 'workspace-settings' });
        break;
    }
    this.pendingStudioTaskReference = null;
    this.studioRouteReady.set(true);
    return true;
  }

  onTaskDetailTabChange(tab: TaskDetailRouteTab): void {
    this.routeDetailTab.set(tab);
  }

  onTaskInspectorTabChange(tab: TaskInspectorRouteTab): void {
    this.routeInspectorTab.set(tab);
  }
  private syncEpicsTabFromHash(): void {
    const rawHash = window.location.hash || '';
    if (!rawHash.startsWith('#epics')) return;
    const rawProject = rawHash.startsWith('#epics:')
      ? decodeURIComponent(rawHash.slice('#epics:'.length))
      : '';
    const projectName = rawProject || null;
    this.studioTabState.open({ kind: 'epics', projectName });
  }

  /**
   * AGT-2067 — settings-gear on the embedded URL preview deep-links to the
   * Project Settings, where URL Preview quick setup is the first section.
   */
  onOpenUrlPreviewSettings(e: { projectName: string }): void {
    this.studioTabState.open({ kind: 'hub', projectName: e.projectName, section: 'settings' });
  }

  private openWorkspaceSettingsInStudio(section: WorkspaceSettingsSection): void {
    // AGT-2035: settings is now purely an editor tab; it no longer also opens a
    // sidebar "Settings" panel (that panel was removed and folded into here).
    this.workspaceOverlays.open(section);
    this.studioTabState.open({ kind: 'workspace-settings' });
  }

  private toggleWorkspaceSettingsInStudio(section: WorkspaceSettingsSection): void {
    const alreadyActive =
      this.studioTabState.activeTab()?.kind === 'workspace-settings' &&
      this.workspaceOverlays.section() === section;
    if (alreadyActive) {
      this.studioTabState.close(studioTabKey({ kind: 'workspace-settings' }));
      this.workspaceOverlays.close();
      return;
    }
    this.openWorkspaceSettingsInStudio(section);
  }

  // Cycle 9g: workspace settings open/close + URL-hash sync delegated to
  // WorkspaceOverlaysService. In Studio layout the same state renders in a
  // normal editor tab; legacy layout keeps the modal shell.
  openWorkspaceSettings(): void {
    if (this.featureFlags.vsCodeLayout()) {
      this.openWorkspaceSettingsInStudio('overview');
      return;
    }
    this.workspaceOverlays.openSettings();
  }
  toggleWorkspaceSettings(): void {
    if (this.featureFlags.vsCodeLayout()) {
      this.toggleWorkspaceSettingsInStudio('overview');
      return;
    }
    this.workspaceOverlays.toggleSettings();
  }
  openWorkspaceTokens(): void {
    if (this.featureFlags.vsCodeLayout()) {
      this.openWorkspaceSettingsInStudio('tokens');
      return;
    }
    this.workspaceOverlays.openTokens();
  }
  closeWorkspaceTokens(): void {
    this.workspaceOverlays.closeTokens();
  }
  openWorkspaceScreenshots(): void {
    if (this.featureFlags.vsCodeLayout()) {
      this.openWorkspaceSettingsInStudio('screenshots');
      return;
    }
    this.workspaceOverlays.openScreenshots();
  }
  closeWorkspaceScreenshots(): void {
    this.workspaceOverlays.closeScreenshots();
  }
  toggleWorkspaceScreenshots(): void {
    if (this.featureFlags.vsCodeLayout()) {
      this.toggleWorkspaceSettingsInStudio('screenshots');
      return;
    }
    this.workspaceOverlays.toggleScreenshots();
  }
  openCliAdmin(): void {
    if (this.featureFlags.vsCodeLayout()) {
      this.openWorkspaceSettingsInStudio('caps');
      return;
    }
    this.workspaceOverlays.openCliAdmin();
  }
  closeCliAdmin(): void {
    this.workspaceOverlays.closeCliAdmin();
  }
  toggleCliAdmin(): void {
    if (this.featureFlags.vsCodeLayout()) {
      this.toggleWorkspaceSettingsInStudio('caps');
      return;
    }
    this.workspaceOverlays.toggleCliAdmin();
  }

  /**
   * "Open task" link inside the workspace reel lightbox: close the
   * reel overlay, navigate the side panel to the screenshot's
   * originating job. Mirrors the open-task pattern used by the
   * orchestrator feed.
   */
  onOpenTaskFromReel(s: Pick<TaskScreenshot, 'jobId' | 'watchPath'>): void {
    this.closeWorkspaceScreenshots();
    if (!s?.jobId || !s?.watchPath) return;
    this.jobService.getDetail(s.jobId, s.watchPath).subscribe({
      next: (detail) => this.jobSelection.selectResolvedDetail(detail, 'push'),
      error: () => {
        /* keep the user where they were */
      },
    });
  }

  /**
   * "By project" usage row clicked inside the CLI-Management usage detail:
   * close the global workspace-settings overlay, then open that project's
   * Settings rail. Routing through `openProjectShell` keeps navigation
   * shell-coordinated rather than letting a leaf component write the hash.
   * The overlay must be closed explicitly first because the project-shell
   * hash push does not fire a hashchange that would auto-close it.
   */
  onOpenProjectSettingsFromUsage(name: string): void {
    if (!name) return;
    this.workspaceOverlays.close();
    this.openProjectShell(name, 'settings');
  }

  /**
   * Session task-link chip clicked inside the CLI-Management "CLI sessions"
   * list: close the global workspace-settings home, then open the owning
   * task's detail panel. Mirrors `onOpenTaskFromReel`; routing through the
   * shell keeps `selectedJob` + the URL query the single owner of detail
   * navigation rather than letting a leaf component drive it.
   */
  onOpenTaskFromSession(ref: { jobId: string; watchPath: string }): void {
    this.workspaceOverlays.close();
    if (!ref?.jobId || !ref?.watchPath) return;
    this.jobService.getDetail(ref.jobId, ref.watchPath).subscribe({
      next: (detail) => this.jobSelection.selectResolvedDetail(detail, 'push'),
      error: () => {
        /* keep the user where they were */
      },
    });
  }

  cancelCreate() {
    this.createJobForm.cancel();
  }
  submitCreate() {
    this.createJobForm.submit();
  }

  toggleProject(event: { name: string; additive: boolean } | string) {
    if (typeof event === 'string') {
      // Legacy call sites (programmatic invocations) keep their toggle
      // semantics so multi-select remains reachable without a modifier key.
      this.boardFilters.toggleProject(event);
      return;
    }
    this.boardFilters.selectProject(event.name, event.additive);
  }
  isProjectActive(name: string): boolean {
    return this.boardFilters.isProjectActive(name);
  }

  // Pre-bound arrow-function aliases for child components that take a
  // predicate-style input (e.g. <app-project-tabs>). Using arrows keeps
  // `this` correct without per-call .bind().
  readonly isProjectActiveFn = (name: string) => this.isProjectActive(name);
  readonly getRunnerIndicatorFn = (name: string) => this.getRunnerIndicator(name);
  readonly getAutoInfoFn = (name: string) => this.getAutoInfo(name);
  readonly getProjectTokenChipFn = (name: string) => this.getProjectTokenChip(name);
  readonly identityFor = (name: string) => projectIdentity(name);

  private getProjectTokenChip(name: string) {
    return buildProjectTokenChip(this.jobService.jobs(), name);
  }

  getRunnerIndicator(name: string): { icon: string; cls: string } | null {
    return projectRunnerIndicator(this.jobService.runnerStatus(), name);
  }

  getAutoInfo(name: string) {
    return projectAutoInfo(this.jobService.runnerStatus(), name);
  }

  onToggleAuto(name: string) {
    const runner = this.jobService.runnerStatus().projects[name];
    const mode = runner?.mode ?? 'manual';
    const newMode =
      mode === 'auto-continuous' || mode === 'auto-single' ? 'paused' : 'auto-continuous';
    this.jobService.setRunnerMode(name, newMode).subscribe({
      next: () => {
        this.jobService.refreshRunnerStatus(true);
        // F6: a short toast on auto-pickup mode flips. The chip itself only
        // changes a small dot/label — easy to miss when the click lands by
        // accident. The toast gives the operator a clear "this just changed"
        // moment.
        const verb = newMode === 'paused' ? 'paused' : 'enabled';
        this.notifications.info(`${name} · auto-pickup ${verb}`, 'Runner mode');
      },
      error: (err) => {
        this.errorDialog.show(err, {
          title: 'Failed to change auto-pickup mode',
          fallbackMessage: 'Failed to change auto-pickup mode',
          source: `Project ${name}`,
        });
      },
    });
  }

  /**
   * Project name to attribute the lane-side auto chip to. The chip is
   * meaningful only when there's a single project in scope: either the
   * board is scoped to one project, or every job in the lane belongs to
   * the same project. Otherwise return null so the chip stays hidden.
   */
  laneAutoProject(state: string, jobs: TaskInfo[]): string | null {
    if (state !== TaskState.Progress) return null;
    // Prefer the board's scoped project (Picker selection); fall back to
    // the only project actually in the lane.
    const tab = this.studioTabState.activeTab();
    if (tab?.kind === 'board' && tab.projectName !== '__all__') {
      return tab.projectName;
    }
    if (jobs.length === 0) return null;
    const first = jobs[0].projectName ?? null;
    if (!first) return null;
    return jobs.every((j) => j.projectName === first) ? first : null;
  }

  /** Project selected by the active board tab; null preserves the explicit workspace-wide board. */
  boardProjectScope(): string | null {
    const tab = this.studioTabState.activeTab();
    return tab?.kind === 'board' && tab.projectName !== '__all__' ? tab.projectName : null;
  }

  /** Current runner mode (lookup mirrors the studio-shell header chip). */
  laneAutoMode(state: string, jobs: TaskInfo[]): string {
    const proj = this.laneAutoProject(state, jobs);
    if (!proj) return 'manual';
    return this.jobService.runnerStatus().projects[proj]?.mode ?? 'manual';
  }

  /**
   * Full runner-status snapshot for the lane's auto project (or null when
   * the lane is not project-scoped). Drives the In-Progress lane's status
   * cluster pills (RUNNING / mode / Q:N).
   */
  laneAutoRunner(state: string, jobs: TaskInfo[]) {
    const proj = this.laneAutoProject(state, jobs);
    if (!proj) return null;
    return this.jobService.runnerStatus().projects[proj] ?? null;
  }

  /**
   * F35: resolve the sort strategy that governs a board lane, for the
   * lane-header indicator + drag gate. The board can mix projects in one
   * lane, so we consider the projects that actually have cards in the lane
   * (falling back to the active project filter for an empty lane). When
   * they agree we return that strategy; when they disagree we return
   * `'mixed'`. Empty string means no strategy data is loaded yet.
   */
  laneSortStrategy(state: string, jobs: TaskInfo[]): string {
    const byProject = this.jobService.laneSortStrategies();
    const laneKey = displayStateToLaneKey(state);
    const projects = new Set<string>();
    for (const j of jobs) {
      if (j.projectName) projects.add(j.projectName);
    }
    if (projects.size === 0) {
      for (const p of this.activeProjects()) projects.add(p);
    }
    const strategies = new Set<string>();
    for (const p of projects) {
      const lane = byProject[p]?.[laneKey];
      if (lane) strategies.add(lane);
    }
    if (strategies.size === 0) return '';
    if (strategies.size === 1) return [...strategies][0];
    return 'mixed';
  }

  /**
   * F35: drag-reorder is permitted on lanes whose resolved strategy is
   * `manual` or `lane-entry`. Under `lane-entry` a drag pins a card in place
   * (explicit order) while the rest of the lane keeps flowing by entry time —
   * so reorder must stay enabled for the override to work. While strategy data
   * is still loading (empty string) we keep the historical behaviour and allow
   * reorder so the board never flickers into a disabled state if the settings
   * endpoint is briefly unavailable.
   */
  laneReorderDisabled(state: string, jobs: TaskInfo[]): boolean {
    const strategy = this.laneSortStrategy(state, jobs);
    if (!strategy) return false;
    return !allowsDragReorder(strategy);
  }

  /**
   * 1-Hz wall-clock tick so the RUNNING pill's elapsed-time string
   * (`3m24s`) advances without re-polling /api/runner/status. The lane
   * column reads this from its `nowMs` input; only the lane that renders
   * the RUNNING pill consumes it.
   */
  readonly nowMs = signal(Date.now());
  private nowMsTickHandle: ReturnType<typeof setInterval> | null = null;

  onFileSaved() {
    this.boardMutations.refreshAfterFileSave();
  }
  onProjectChanged(targetWatchPath: string) {
    this.boardMutations.reopenAfterProjectChange(targetWatchPath);
  }

  closeErrorDialog() {
    this.errorDialog.close();
  }

  copyErrorDetails() {
    this.errorDialog.copyActiveError();
  }

  copyErrorButtonLabel(): string {
    switch (this.errorDialog.copyState()) {
      case 'copied':
        return 'Copied';
      case 'failed':
        return 'Copy failed';
      default:
        return 'Copy output';
    }
  }

  openCliConfigFromError() {
    if (!this.selectedJobUsesCopilot()) return;
    this.errorDialog.requestCliConfig();
  }

  // Side-sheet width and collapse functionality
  toggleGroupCollapse(state: string) {
    const current = new Set(this.collapsedGroups());
    if (current.has(state)) {
      current.delete(state);
    } else {
      current.add(state);
    }
    this.collapsedGroups.set(current);
    localStorage.setItem('collapsedGroups', JSON.stringify([...current]));
  }

  isGroupCollapsed(state: string): boolean {
    return this.collapsedGroups().has(state);
  }

  // Cycle 9: per-lane collapse + container focus methods delegate to LaneCollapseService.
  // The shell forwards the lane-id list so the service stays free of
  // the kanban catalogue shape; everything else is straight pass-through.

  toggleLaneCollapse(state: string): void {
    this.laneCollapse.toggleLaneCollapse(state);
  }
  isLaneCollapsed(state: string): boolean {
    return this.laneCollapse.isLaneCollapsed(state);
  }
  expandedLaneCount(group: { lanes: { state: string }[] }): number {
    return this.laneCollapse.expandedLaneCount(group);
  }
  isContainerFocused(id: string): boolean {
    return this.laneCollapse.isContainerFocused(id);
  }
  toggleContainerFocus(id: string): void {
    this.laneCollapse.toggleContainerFocus(
      id,
      this.laneGroups().map((g) => g.id),
    );
  }
  clearContainerFocus(): void {
    this.laneCollapse.clearContainerFocus();
  }

  // Cycle 9: UI-pref methods delegate to UiPreferencesService.
  setTaskNavCollapsed(collapsed: boolean): void {
    this.uiPrefs.setTaskNavCollapsed(collapsed);
  }
  startResize(event: MouseEvent): void {
    this.uiPrefs.startResize(event);
  }
}
