import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  HostListener,
  ViewEncapsulation,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
  viewChildren,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import type { TaskInfo, RegistryWorkspaceListItem, RegistryProjectSummary, WatchPathEntry, RegistryProjectUrl } from '../../models/task.model';
import type { WorkbenchListItem } from '../../models/project-docs.model';
import { forkJoin } from 'rxjs';
import { TaskService } from '../../services/task.service';
import { StudioIconComponent } from '../../components/studio-icon/studio-icon.component';
import { StudioSidebarHeaderComponent } from '../../components/studio-sidebar-header/studio-sidebar-header.component';
import { EmptyStateComponent } from '../../components/empty-state/empty-state.component';
import { StudioWelcomeComponent } from './components/studio-welcome/studio-welcome.component';
import { SectionHeaderComponent } from '../../components/section-header/section-header.component';
import { CountBadgeComponent } from '../../components/count-badge/count-badge.component';
import { ListRowComponent } from '../../components/list-row/list-row.component';
import { ClientService } from '../../services/client.service';
import { FeatureFlagsService } from '../../services/feature-flags.service';
import { projectIdentity } from '../../services/project-identity.util';
import { TaskSelectionService } from '../task-detail';
import { ProjectDetailComponent } from '../project-detail';
import {
  PROJECT_RAIL_ITEMS,
  DEFAULT_PROJECT_RAIL_KEY,
  isProjectRailKey,
  type ProjectRailItem,
} from '../project-detail/components/project-shell/project-shell.config';
import type { StudioIconName } from '../../components/studio-icon/studio-icon.component';
import { BoardFiltersService, flattenGrouped } from '../board';
import { ConfirmDialogService } from '../../services/confirm-dialog.service';
import { NotificationService } from '../../services/notification.service';
import { copyTextToClipboard } from '../../services/clipboard.util';
import { WorkspaceManagerService, ProjectDragDropService, WorkspaceOverlaysService, UiPreferencesService } from '../shell';
import { ProjectLookupService } from '../../services/project-lookup.service';
import { ThemeService } from './services/theme.service';
import { StudioActivityBarComponent, StudioActivityBarItem, StudioActivityPanelKey } from './components/studio-activity-bar/studio-activity-bar.component';
import { resolveActiveActivityKey } from './components/studio-activity-bar/studio-activity-bar.active-key';
import { ExplorerWorkspaceTreeComponent, type ExplorerProjectSurface } from './components/explorer-workspace-tree/explorer-workspace-tree.component';
import {
  deriveProjectAutoPickupByName,
  type ProjectAutoPickupIndicator,
} from './studio-shell.auto-pickup';
import { MenuComponent, MenuItem, MenuItemClickEvent } from '../../components/menu';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { AppTooltipDirective } from '../../components/tooltip/app-tooltip.directive';
import { TaskStatusPopoverDirective } from '../../components/task-status-card';
import { buildProjectPickerItems, buildTabCtxMenuItems } from './studio-shell.menu-builders';
import { StudioTabStateService } from './services/studio-tab-state.service';
import { StudioPanelStateService } from './services/studio-panel-state.service';
import { ExplorerSectionsService } from './services/explorer-sections.service';
import { ExplorerWorkbenchStateService } from './services/explorer-workbench-state.service';
import { buildProjectSidebarRows, type ProjectSidebarRow } from './studio-shell.project-rows';
import { StudioTab, studioTabKey } from './studio-shell.types';
import { GlobalSearchComponent } from './components/global-search/global-search.component';
import { OrchestratorFeedStore } from '../orchestrator';

/** Canonicalise project storage paths so titlebar workspace lookup survives
 * slash style, trailing separator, and case differences. */
function normalizeStorage(path: string): string {
  return path.replace(/[\\/]+/g, '/').replace(/\/+$/, '').toLowerCase();
}


/** Brand swatches per CLI — matches the status-bar glyph colours so the
 *  Sidebar CLI panel reads the same on first glance. */
function cliColorFor(cli: string): string {
  switch (cli) {
    case 'claude':  return '#d97757';
    case 'codex':   return '#569cd6';
    case 'gemini':  return '#c586c0';
    default:        return '#6e6e6e';
  }
}


/**
 * Top-level "Agent Software Studio" shell — the VS-Code-inspired chrome
 * that replaces the legacy single-pane layout. Behind the `vsCodeLayout`
 * feature flag for now; flip to default once the rest of the views
 * (Deck, full-screen diff, full-screen activity) are migrated.
 *
 * Owns the chrome (titlebar / activity bar / sidebar host / tab host /
 * status bar) and delegates state to the studio-shell services so child
 * panels and tabs can read it without prop drilling. Existing feature
 * components (`<app-job-column>`, `<app-job-detail>`, …) render inside
 * the tab area unchanged — this component is a wrapper, not a rewrite.
 */
@Component({
  selector: 'app-studio-shell',
  standalone: true,
  imports: [FormsModule, StudioIconComponent, StudioSidebarHeaderComponent, EmptyStateComponent, StudioWelcomeComponent, SectionHeaderComponent, CountBadgeComponent, ListRowComponent, StudioActivityBarComponent, MenuComponent, TooltipDirective, AppTooltipDirective, TaskStatusPopoverDirective, ExplorerWorkspaceTreeComponent, ProjectDetailComponent, GlobalSearchComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  encapsulation: ViewEncapsulation.None,
  templateUrl: './studio-shell.component.html',
  styleUrl: './studio-shell.component.scss',
})
export class StudioShellComponent {
  private readonly jobService = inject(TaskService);
  readonly clientService = inject(ClientService);

  /**
   * Backend-known project names (from the WatchPaths config). The
   * shell uses this so projects with zero working-set jobs still
   * appear in the picker / explorer. Defaults to [] so legacy hosts
   * that don't pass it keep their job-derived behaviour.
   */
  readonly knownProjectNames = input<readonly string[]>([]);

  /** Backend WatchPaths feeding the rename-stable `projectStorageByName` join
   *  (a row's storage == registry `storageLocation`, stable across rename). */
  readonly projectWatchPaths = input<readonly WatchPathEntry[]>([]);

  /**
   * Host-computed badge for the Filters activity-bar item. The host owns
   * the distinction between real filters and view/scope state, while this
   * shell owns the rail button that renders the count.
   */
  readonly filterBadgeCount = input<number | null>(null);

  /** Emitted after a delete so the host re-pulls WatchPaths + refreshes. */
  readonly projectDeleted = output<void>();

  private readonly featureFlags = inject(FeatureFlagsService);
  private readonly tabState = inject(StudioTabStateService);
  private readonly panelState = inject(StudioPanelStateService);
  private readonly jobSelection = inject(TaskSelectionService);
  readonly uiPrefs = inject(UiPreferencesService);
  readonly boardFilters = inject(BoardFiltersService);
  readonly explorerSections = inject(ExplorerSectionsService);
  private readonly explorerWorkbenchState = inject(ExplorerWorkbenchStateService);
  private readonly confirmDialog = inject(ConfirmDialogService);
  private readonly notifications = inject(NotificationService);
  private readonly workspaceManager = inject(WorkspaceManagerService);
  private readonly workspaceOverlays = inject(WorkspaceOverlaysService);
  private readonly projectLookup = inject(ProjectLookupService);
  private readonly themeService = inject(ThemeService);
  private readonly orchestratorFeed = inject(OrchestratorFeedStore);

  /** Tab list + active selection re-exposed for the template. */
  readonly tabs = this.tabState.tabs;
  readonly activeKey = this.tabState.activeKey;
  readonly activeTab = this.tabState.activeTab;
  readonly tabKey = studioTabKey;
  private readonly tabElements = viewChildren<ElementRef<HTMLElement>>('studioTab');

  /** Keep every newly activated editor tab inside the horizontally scrolling
   *  strip without moving the page or disturbing an already visible tab. */
  private readonly keepActiveTabVisibleFx = effect(() => {
    const activeKey = this.activeKey();
    const tabs = this.tabs();
    if (!activeKey || !tabs.some(tab => studioTabKey(tab) === activeKey)) return;

    const activeElement = this.tabElements()
      .find(ref => ref.nativeElement.dataset['tabKey'] === activeKey)
      ?.nativeElement;
    if (!activeElement || typeof activeElement.scrollIntoView !== 'function') return;

    const listRect = activeElement.parentElement?.getBoundingClientRect();
    const activeRect = activeElement.getBoundingClientRect();
    if (!listRect || (activeRect.left >= listRect.left && activeRect.right <= listRect.right)) return;
    activeElement.scrollIntoView({
      behavior: 'smooth',
      block: 'nearest',
      inline: 'nearest',
    });
  });

  /** Sidebar panel state re-exposed for the template. */
  readonly activePanel = this.panelState.active;
  readonly sidebarVisible = this.panelState.visible;
  readonly sidebarWidth = this.panelState.sidebarWidth;
  readonly activityBarSide = this.panelState.activityBarSide;
  readonly globalSearchOpen = signal(false);
  /**
   * Which Explorer-tree project rows are expanded (showing Board / Project
   * Deck / Activity sub-items). Persists across reloads so the user's
   * preferred tree shape survives an F5.
   */
  private readonly _expandedProjects = signal<Set<string>>(
    new Set(this.readExpandedProjects()),
  );
  readonly expandedProjects = this._expandedProjects.asReadonly();
  readonly collapseAllVersion = signal(0);

  toggleProjectExpanded(name: string, event?: Event): void {
    event?.stopPropagation();
    this._expandedProjects.update(set => {
      const next = new Set(set);
      if (next.has(name)) next.delete(name);
      else next.add(name);
      this.writeExpandedProjects(next);
      return next;
    });
  }

  private readExpandedProjects(): string[] {
    if (typeof window === 'undefined') return [];
    try {
      const raw = window.localStorage?.getItem('atp.studio.explorer.expanded');
      if (!raw) return [];
      const arr = JSON.parse(raw) as unknown;
      if (Array.isArray(arr)) return arr.filter((s): s is string => typeof s === 'string');
      return [];
    } catch {
      return [];
    }
  }

  private writeExpandedProjects(set: Set<string>): void {
    if (typeof window === 'undefined') return;
    try {
      window.localStorage?.setItem('atp.studio.explorer.expanded', JSON.stringify([...set]));
    } catch {
      /* storage may be full / blocked */
    }
  }

  /**
   * Drives the "moon / sun" icon in the titlebar. Theme is owned by the global
   * {@link ThemeService} (AGT-2035) so this quick-toggle and the Appearance
   * settings row stay in sync; the shell just re-exposes the read signal.
   */
  readonly theme = this.themeService.theme;

  /** Bubbles to app.ts so the parent can flip the orchestrator side
   *  sheet open without the shell needing a reference to it. */
  readonly chatToggle = output<void>();
  readonly openUsageSheet = output<void>();
  readonly openCliAdmin = output<void>();
  readonly openWorkspaceScreenshots = output<void>();
  readonly openOrchFeed = output<void>();
  readonly openOrchSettings = output<void>();
  /** Emits when the user toggles the auto-pickup mode for a project. */
  readonly toggleAuto = output<string>();

  /** Project picker dropdown open state. */
  readonly pickerOpen = signal(false);

  togglePickerMenu(ev: Event): void {
    ev.stopPropagation();
    this.pickerOpen.update(v => !v);
  }
  closePickerMenu(): void { this.pickerOpen.set(false); }

  /**
   * Centralised click handler for project-picker entries. `name === null`
   * means "All projects" (clears the active project filter); otherwise the
   * named project becomes the active board. `openHub` flag promotes the
   * click to a Deck open (double-click affordance).
   */
  pickProject(name: string | null, openHub = false): void {
    this.closePickerMenu();
    if (name === null) { this.openBoard('__all__'); return; }
    if (openHub) { this.openHub(name); return; }
    this.openBoard(name);
  }

  /** Closes the picker when the user clicks anywhere else in the document. */
  @HostListener('document:click')
  onDocumentClick(): void { this.closePickerMenu(); }

  /**
   * Active project for the picker — derived from the active tab / board.
   * `null` means the user is in "All projects" mode (workspace-wide).
   */
  readonly activeProjectName = computed<string | null>(() => {
    const tab = this.activeTab();
    if (!tab) return null;
    if (tab.kind === 'board') return tab.projectName === '__all__' ? null : tab.projectName;
    if (tab.kind === 'epics') return tab.projectName;
    if (tab.kind === 'workbenches') return tab.projectName;
    return this.currentProjectName();
  });

  readonly activeWorkbench = computed(() => {
    const tab = this.activeTab();
    return tab?.kind === 'workbench'
      ? { projectName: tab.projectName, workbenchId: tab.workbenchId }
      : null;
  });

  readonly activeProjectInitial = computed<string>(() => {
    const name = this.activeProjectName();
    if (!name) return '';
    return projectIdentity(name).initial;
  });

  readonly activeProjectColor = computed<string>(() => {
    const name = this.activeProjectName();
    if (!name) return 'var(--studio-fg-muted)';
    return projectIdentity(name).color;
  });

  readonly activeProjectTotalJobs = computed<number>(() => {
    const name = this.activeProjectName();
    if (!name) return 0;
    return this.projectRows().find(r => r.name === name)?.totalJobs ?? 0;
  });

  activeProjectPickerLabel(): string {
    return this.activeProjectName() ?? 'All projects';
  }

  readonly totalProjectJobs = computed<number>(() =>
    this.projectRows().reduce((sum, r) => sum + r.totalJobs, 0)
  );

  /** Reactive map of project name → current runner mode. */
  autoModeFor(name: string): string {
    return this.jobService.runnerStatus().projects[name]?.mode ?? 'manual';
  }

  /** Project name to effective auto-pickup mode plus admission-gate reason. */
  readonly projectAutoPickupByName = computed<ReadonlyMap<string, ProjectAutoPickupIndicator>>(() =>
    deriveProjectAutoPickupByName(
      this.jobService.runnerStatus().projects,
      this.jobService.projectPickupGates(),
      this.projectRows(),
    ),
  );

  /** Short label for the auto-mode chip ("auto", "single", "paused", "manual"). */
  autoModeLabelFor(name: string): string {
    const mode = this.autoModeFor(name);
    switch (mode) {
      case 'auto-continuous': return 'Auto';
      case 'auto-single':     return 'Auto · 1';
      case 'paused':          return 'Paused';
      default:                return 'Manual';
    }
  }

  autoToggleTooltip(name: string): string {
    const mode = this.autoModeFor(name);
    if (mode === 'auto-continuous' || mode === 'auto-single') {
      return `Auto-pickup is on for ${name} — click to pause.`;
    }
    return `Auto-pickup is paused for ${name} — click to enable.`;
  }


  /**
   * F47 / ADR-0042 — registry-backed workspace list rendered by the
   * Settings panel "Workspaces" section. F45b mutation surface lives
   * inline in this component: see `createRegistryWorkspace`,
   * `renameRegistryWorkspace`, `editRegistryWorkspaceColor`,
   * `moveRegistryWorkspace`, `deleteRegistryWorkspace`.
   */
  readonly registryWorkspaces = signal<readonly RegistryWorkspaceListItem[]>([]);
  readonly registryProjects = signal<readonly RegistryProjectSummary[]>([]);
  readonly registryWorkspacesLoading = signal(false);
  readonly registryWorkspacesError = signal<string | null>(null);
  /** Ids waiting on an Explorer-tree registry mutation (rename / delete), used
   *  to disable the affected row while the request is in flight. Registry
   *  *management* moved to the settings view; these stay for the tree's own
   *  inline rename + delete handlers below. */
  readonly registryWorkspaceBusyId = signal<string | null>(null);
  readonly registryProjectBusyId = signal<string | null>(null);

  /**
   * Lazy-load the registry workspaces whenever the Explorer panel (F46
   * two-level workspace tree) is visible, then re-pull on every re-open so a
   * mutation from another tab is reflected without a full reload. The Explorer
   * is the default panel, so this also primes the tree on boot — without it the
   * tree would fall back to the single legacy "Workspace" folder because
   * `registryWorkspaces()` would stay empty. Registry *management* now lives in
   * the consolidated settings view (AGT-2035), which loads its own copy.
   */
  private readonly loadRegistryWorkspacesFx = effect(() => {
    const panel = this.activePanel();
    const visible = this.sidebarVisible();
    if (!visible || panel !== 'explorer') return;
    this.reloadRegistryWorkspaces();
  });

  /** Reload the registry workspace list whenever the create-dialog or
   *  delete path bumps the counter via WorkspaceManagerService. */
  private readonly registryChangedFx = effect(() => {
    const rev = this.workspaceManager.registryChanged();
    if (rev === 0) return;
    untracked(() => this.reloadRegistryWorkspaces());
  });

  /** Retarget open project-keyed tabs before the registry refresh purges stale names. */
  private readonly projectRenamedFx = effect(() => {
    const rename = this.workspaceManager.projectRenamed();
    if (!rename) return;
    this.tabState.renameProject(rename.previousName, rename.currentName);
  });

  reloadRegistryWorkspaces(): void {
    this.registryWorkspacesLoading.set(true);
    this.registryWorkspacesError.set(null);
    forkJoin({
      workspaces: this.jobService.getRegistryWorkspaces({ includeArchived: false }),
      projects: this.jobService.getRegistryProjects({ includeArchived: false }),
    }).subscribe({
      next: ({ workspaces: ws, projects }) => {
        this.registryWorkspaces.set(ws ?? []);
        this.registryProjects.set(projects ?? []);
        this.projectLookup.setWorkspaces(ws ?? []);
        this.registryWorkspacesLoading.set(false);
      },
      error: (err: unknown) => {
        this.registryWorkspacesError.set(this.errMsg(err));
        this.registryWorkspacesLoading.set(false);
      },
    });
  }

  /** Registry-only project patch used by the Explorer tree's inline handlers
   *  (rename / short-code). Registry *management* lives in the settings view;
   *  this stays for the tree's own edit affordances. */
  private runProjectPatch(projId: string, patch: {
    displayName?: string;
    shortCode?: string;
    color?: string | null;
    clearColor?: boolean;
    workspaceId?: string;
    archived?: boolean;
  }): void {
    this.registryProjectBusyId.set(projId);
    this.jobService.updateRegistryProject(projId, patch).subscribe({
      next: () => { this.registryProjectBusyId.set(null); this.reloadRegistryWorkspaces(); },
      error: (err: unknown) => {
        this.registryProjectBusyId.set(null);
        this.registryWorkspacesError.set(this.errMsg(err));
      },
    });
  }

  private errMsg(err: unknown): string {
    if (err && typeof err === 'object' && 'error' in err) {
      const inner = (err as { error?: unknown }).error;
      if (inner && typeof inner === 'object' && 'error' in inner)
        return String((inner as { error?: unknown }).error);
      if (typeof inner === 'string') return inner;
    }
    if (err instanceof Error) return err.message;
    return 'Request failed';
  }

  private lastAutoExpandedNavigationPath: string | null = null;

  /**
   * Auto-expand the active project in the Explorer tree so the lane
   * children (backlog / active / human review / Deck / archive)
   * are visible the moment the user opens a board or task. Matches the
   * agent-orchestrator.zip mockup, which always shows the active project
   * expanded.
   *
   * Only acts when the active destination changes. A manual collapse does not
   * alter that destination key, so the user's choice remains intact until the
   * next navigation. Other project branches are never changed.
   */
  private readonly autoExpandActivePathFx = effect(() => {
    const name = this.activeProjectName();
    const surface = this.activeProjectSurface();
    const workbenchId = this.activeWorkbench()?.workbenchId ?? '';
    if (!name || !surface) {
      this.lastAutoExpandedNavigationPath = null;
      return;
    }
    const navigationPath = `${name}:${surface}:${workbenchId}`;
    if (this.lastAutoExpandedNavigationPath === navigationPath) return;
    this.lastAutoExpandedNavigationPath = navigationPath;
    untracked(() => {
      if (this._expandedProjects().has(name)) return;
      this._expandedProjects.update(set => {
        const next = new Set(set);
        next.add(name);
        this.writeExpandedProjects(next);
        return next;
      });
    });
  });

  /** All jobs, grouped under their project for the Explorer panel. */
  readonly grouped = this.jobService.grouped;
  readonly globalSearchTasks = computed(() => flattenGrouped(this.grouped()));

  /**
   * Concrete workspace shown in the titlebar breadcrumb. The breadcrumb no
   * longer exposes the generic create-workspace affordance or the active tab
   * kind; the project picker beside it carries the current project and runner
   * status.
   */
  readonly activeWorkspaceName = computed<string | null>(() => {
    const projectName = this.currentProjectName();
    if (!projectName) return null;
    const storage = this.projectStorageByName().get(projectName);
    const normalizedStorage = storage ? normalizeStorage(storage) : null;
    for (const ws of this.registryWorkspaces()) {
      const matched = ws.projects.some(p =>
        p.displayName === projectName ||
        (!!normalizedStorage && normalizeStorage(p.storageLocation) === normalizedStorage)
      );
      if (matched) return ws.displayName;
    }
    return null;
  });

  /** Project rows displayed in the titlebar pills + sidebar Explorer.
   *  A2 (2026-05-21): the visible "open jobs" counter excludes the
   *  archive lane. Archive grows monotonically with E2E fixtures /
   *  completed runs and was inflating the count (e.g. "Agent Software
   *  Studio: 447" → working set ~67 + ~380 archived). Operator now
   *  sees the working-set count by default; the picker dropdown can
   *  surface the full total separately when needed.
   *
   *  D5 follow-up: also include projects that the backend knows about
   *  but have zero working-set jobs (a fresh sandbox like
   *  `Playwright Test` lives entirely in 7-archive or has nothing yet —
   *  it must still render as a picker target for the probe to land
   *  tasks). The set of "known projects" comes from
   *  `TaskService.getWatchPaths()` via the shell's `projectNames` input
   *  the host passes in app.html. */
  readonly projectRows = computed<ProjectSidebarRow[]>(() => {
    return buildProjectSidebarRows(this.grouped(), this.knownProjectNames(), this.currentProjectName());
  });

  /** Row name → resolved storage path for the tree's rename-stable join. */
  readonly projectStorageByName = computed<ReadonlyMap<string, string>>(() => {
    const map = new Map<string, string>();
    for (const wp of this.projectWatchPaths()) {
      if (wp.name && wp.path) map.set(wp.name, wp.path);
    }
    return map;
  });

  /** Project display name → registry shortCode (e.g. "Agent Software Studio"
   *  → "ASS"), used to keep project-scoped tab titles short and scannable. */
  readonly projectShortCodeByName = computed<ReadonlyMap<string, string>>(() => {
    const map = new Map<string, string>();
    for (const ws of this.registryWorkspaces()) {
      for (const p of ws.projects) {
        if (p.displayName && p.shortCode) map.set(p.displayName, p.shortCode);
      }
    }
    return map;
  });

  /** Project name driving the currently open Board tab (or null when none). */
  readonly activeBoardProject = computed<string | null>(() => {
    const tab = this.activeTab();
    if (tab?.kind === 'board') return tab.projectName === '__all__' ? null : tab.projectName;
    return null;
  });

  readonly activeProjectSurface = computed<ExplorerProjectSurface | null>(() => {
    const tab = this.activeTab();
    if (!tab) return null;
    if (tab.kind === 'board') return tab.projectName === '__all__' ? null : 'board';
    if (tab.kind === 'hub') return tab.section === 'wiki' ? 'wiki' : 'hub';
    if (tab.kind === 'workbenches') return tab.projectName === null ? null : 'workbenches';
    if (tab.kind === 'workbench') return 'workbench';
    if (tab.kind === 'epics') return tab.projectName === null ? null : 'epics';
    return null;
  });

  /**
   * The project the application is scoped to. This drives the active titlebar
   * pill and the default project for sidebar CTAs. Board/Deck tabs name a
   * project directly. Task/Activity tabs preserve the existing board-filter
   * scope because their owning project is only a detail data handle.
   */
  readonly currentProjectName = computed<string | null>(() => {
    const tab = this.activeTab();
    if (!tab) return null;
    if (tab.kind === 'board') return tab.projectName === '__all__' ? null : tab.projectName;
    if (tab.kind === 'epics') return tab.projectName;
    if (tab.kind === 'workbenches') return tab.projectName;
    if (tab.kind === 'hub') return tab.projectName;
    if (tab.kind === 'workbench') return tab.projectName;
    if (tab.kind === 'task' || tab.kind === 'activity') {
      const projects = [...this.boardFilters.activeProjects()];
      return projects.length === 1 ? projects[0] : null;
    }
    return null;
  });

  readonly activityBarItems: readonly StudioActivityBarItem[] = [
    { key: 'explorer', icon: 'folder', label: 'Explorer' },
    { key: 'filters', icon: 'filter', label: 'Filters' },
    { key: 'cli', icon: 'cli', label: 'Agents / CLI' },
    { key: 'activity', icon: 'activity', label: 'Activity' },
    { key: 'runbook', icon: 'runbook', label: 'Runbook' },
  ];

  /**
   * Single source of truth for the ActivityBar's active marker (AGT-2042).
   * The sidebar toggle (`activePanel` + `sidebarVisible`) and the editor
   * route (`activeTab().kind`) used to light up buttons independently, so
   * two could be active at once. Funnelling both through one resolved key
   * makes the marker exclusive by construction — a button is active iff its
   * key equals this value.
   */
  readonly activeActivityKey = computed(() =>
    resolveActiveActivityKey({
      activeTabKind: this.activeTab()?.kind,
      activePanel: this.activePanel(),
      sidebarVisible: this.sidebarVisible(),
    }),
  );

  readonly activityBarBadgeCounts = computed<Record<string, number>>(() => ({
    filters: this.filterBadgeCount()
      ?? this.boardFilters.activeFilterCount(),
    activity: this.orchestratorFeed.freshAlertCount(),
  }));

  private readonly feedSeenFx = effect(() => {
    this.orchestratorFeed.freshAlertCount();
    if (this.activeTab()?.kind !== 'feed') return;
    untracked(() => this.orchestratorFeed.markAlertsSeen());
  });

  openBoard(projectName: string): void {
    this.tabState.open({ kind: 'board', projectName });
  }

  openFeed(): void {
    this.orchestratorFeed.markAlertsSeen();
    this.tabState.open({ kind: 'feed' });
  }

  openChatHistory(): void {
    this.tabState.open({ kind: 'chat-history' });
  }

  /**
   * Activity-bar Epics button click. Opens or focuses the workspace-wide
   * epic overview as a normal editor tab.
   */
  onActivityBarOpenEpics(): void {
    this.tabState.open({ kind: 'epics', projectName: null });
  }

  /**
   * Whether any epic cards exist across all projects. Drives the
   * activity-bar Epics button visibility (hide-when-empty), so projects
   * that don't use epics never see the entry point.
   */
  readonly hasEpics = computed(() =>
    flattenGrouped(this.grouped()).some(t => t.kind === 'epic'),
  );

  openTask(job: TaskInfo): void {
    this.tabState.open({ kind: 'task', taskKey: job.taskKey });
    // Keep the legacy TaskSelectionService in sync so the embedded
    // <app-job-detail> can pick the job up by reading the selected signal.
    this.jobSelection.openDetailAfterPaint(job);
  }

  openHub(projectName: string): void {
    // Clicking "Project" is an explicit request for the Overview rail, even
    // when the Deck tab is already open on another section (e.g. Wiki). We
    // pass section=overview rather than leaving it undefined so the intent is
    // unambiguous and re-opening moves the rail back to Overview.
    this.tabState.open({ kind: 'hub', projectName, section: 'overview' });
  }

  /**
   * AGT-2067 — open (or focus) a Project URL's embedded preview tab. Replaces
   * the old `window.open` browser jump on the Explorer URL row: the URL now
   * renders inside a sandboxed iframe as its own editor tab (one tab per URL),
   * so it docks beside the Orchestrator side sheet like every other surface.
   */
  openUrlPreview(e: { projectName: string; urlId: string }): void {
    this.tabState.open({ kind: 'url-preview', projectName: e.projectName, urlId: e.urlId });
  }

  /**
   * Explorer "Wiki" link for a single project. Opens (or focuses) the
   * project's Deck tab deep-linked to its Wiki rail, so the wiki is
   * reachable as a top-level sidebar item under Deck.
   */
  openWiki(projectName: string): void {
    this.tabState.open({ kind: 'hub', projectName, section: 'wiki' });
  }

  openWorkbench(event: { projectName: string; workbench: WorkbenchListItem }): void {
    this.tabState.open({
      kind: 'workbench',
      projectName: event.projectName,
      workbenchId: event.workbench.id,
      title: event.workbench.title,
      key: event.workbench.key ?? undefined,
    });
  }

  openWorkbenches(projectName: string | null): void {
    this.tabState.open({ kind: 'workbenches', projectName });
  }

  /**
   * Explorer "Epics" link for a single project (ASS-658). Opens the scoped
   * epic overview as a normal editor tab.
   */
  openProjectEpics(projectName: string): void {
    this.tabState.open({ kind: 'epics', projectName });
  }

  selectTab(key: string): void {
    this.tabState.select(key);
  }

  closeTab(key: string, event?: Event): void {
    event?.stopPropagation();
    this.tabState.close(key);
    this.closeWorkspaceSettingsStateIfTabMissing();
  }

  onTabAuxClick(event: MouseEvent, key: string): void {
    if (event.button !== 1) return;
    event.preventDefault();
    this.closeTab(key, event);
  }

  closeOthers(key: string): void {
    this.tabState.closeOthers(key);
    this.closeWorkspaceSettingsStateIfTabMissing();
  }
  closeRight(key: string): void {
    this.tabState.closeRight(key);
    this.closeWorkspaceSettingsStateIfTabMissing();
  }
  closeLeft(key: string): void {
    this.tabState.closeLeft(key);
    this.closeWorkspaceSettingsStateIfTabMissing();
  }
  closeAll(): void {
    this.tabState.closeAll();
    this.closeWorkspaceSettingsStateIfTabMissing();
  }

  private closeWorkspaceSettingsStateIfTabMissing(): void {
    if (!this.tabs().some(tab => studioTabKey(tab) === 'workspace-settings')) {
      this.workspaceOverlays.close();
    }
  }

  // ---- drag-reorder ---------------------------------------------------
  // Tracks which tab is currently being dragged and which tab the
  // pointer is hovering over so the template can render an insertion-
  // marker line + a "ghosted" source row.

  readonly draggingTabKey = signal<string | null>(null);
  /** Key the drop-marker is rendered before. `__end__` = after the last tab. */
  readonly dragOverTabKey = signal<string | null>(null);

  onTabDragStart(event: DragEvent, key: string): void {
    if (!event.dataTransfer) return;
    event.dataTransfer.effectAllowed = 'move';
    // The serialized payload isn't read back (we keep the source key in a
    // signal), but Firefox refuses to start a drag without setData.
    try { event.dataTransfer.setData('text/x-studio-tab', key); } catch { /* ignore */ }
    this.draggingTabKey.set(key);
  }

  onTabDragOver(event: DragEvent, overKey: string): void {
    if (!this.draggingTabKey()) return;
    event.preventDefault();
    if (event.dataTransfer) event.dataTransfer.dropEffect = 'move';
    if (this.dragOverTabKey() !== overKey) {
      this.dragOverTabKey.set(overKey);
    }
  }

  onTabDragLeave(_event: DragEvent, overKey: string): void {
    if (this.dragOverTabKey() === overKey) {
      this.dragOverTabKey.set(null);
    }
  }

  onTabDrop(event: DragEvent, overKey: string): void {
    event.preventDefault();
    const source = this.draggingTabKey();
    this.draggingTabKey.set(null);
    this.dragOverTabKey.set(null);
    if (!source || source === overKey) return;
    this.tabState.move(source, overKey);
  }

  onTabDragEnd(event: DragEvent): void {
    void event;
    this.draggingTabKey.set(null);
    this.dragOverTabKey.set(null);
  }

  // ---- project drag-and-drop -----------------------------------------
  // F46: drag a project row onto a (different, real) workspace folder to
  // reassign the project's registry workspace membership. The drag
  // lifecycle + drop-validity live in `ProjectDragDropService`; the shell
  // owns the persistence because it already holds the registry-reload path.
  // No job folder is moved on disk — the registry is the source of truth
  // for the tree's grouping, so reloading it re-homes the row (ADR-0048).

  readonly projectDrag = inject(ProjectDragDropService);
  readonly moveErrorMessage = this.projectDrag.moveErrorMessage;

  onProjectWorkspaceDrop(e: { projectId: string; targetWorkspaceId: string }): void {
    this.projectDrag.movingProjectId.set(e.projectId);
    this.projectDrag.moveErrorMessage.set(null);
    this.jobService.updateRegistryProject(e.projectId, { workspaceId: e.targetWorkspaceId }).subscribe({
      next: () => {
        this.projectDrag.movingProjectId.set(null);
        this.reloadRegistryWorkspaces();
      },
      error: (err: unknown) => {
        this.projectDrag.movingProjectId.set(null);
        this.projectDrag.moveErrorMessage.set(this.errMsg(err));
      },
    });
  }

  /**
   * F46 — persist a workspace rename committed from the Explorer tree's inline
   * editor. Registry-only mutation (ADR-0048): no project folder is moved or
   * renamed on disk. Reload makes the new name visible in the tree header.
   */
  onTreeRenameWorkspace(e: { id: string; displayName: string }): void {
    this.registryWorkspaceBusyId.set(e.id);
    this.jobService.updateRegistryWorkspace(e.id, { displayName: e.displayName }).subscribe({
      next: () => { this.registryWorkspaceBusyId.set(null); this.reloadRegistryWorkspaces(); },
      error: (err: unknown) => {
        this.registryWorkspaceBusyId.set(null);
        this.registryWorkspacesError.set(this.errMsg(err));
      },
    });
  }

  /** F46 step 7 — registry-only project rename (ADR-0042): PROJ id + on-disk
   *  storage untouched, so tasks/IDs/keys stay intact; reload shows it at once. */
  onTreeRenameProject(e: { projectId: string; displayName: string }): void {
    this.runProjectPatch(e.projectId, { displayName: e.displayName });
  }

  /**
   * F46 — destructive project delete from the tree's right-click menu, guarded
   * by two confirm stages (plain danger, then a type-to-confirm gate). Only
   * after both pass do we DELETE /api/projects/{projId}; the backend removes
   * storage folder + WatchPaths entry + registry record atomically (no orphan).
   */
  async onTreeDeleteProject(e: { projectId: string; displayName: string; shortCode: string | null }): Promise<void> {
    const label = e.displayName || e.projectId;
    const stageOne = await this.confirmDialog.confirm({
      title: 'Delete this project?',
      message: 'This permanently deletes the project and all of its tasks from disk. This cannot be undone.',
      detail: `${label} (${e.projectId})`,
      confirmLabel: 'Continue',
      cancelLabel: 'Cancel',
      kind: 'danger',
    });
    if (!stageOne) return;

    const typedValues = e.shortCode ? [e.displayName, e.shortCode] : [e.displayName];
    const hint = e.shortCode
      ? `Type the project name "${e.displayName}" or its code "${e.shortCode}" to confirm.`
      : `Type the project name "${e.displayName}" to confirm.`;
    const stageTwo = await this.confirmDialog.confirm({
      title: 'Confirm permanent deletion',
      message: 'Last step. Re-type the project to delete it and its entire storage folder.',
      requireTypedValues: typedValues,
      requireTypedPrompt: hint,
      confirmLabel: 'Delete project',
      cancelLabel: 'Cancel',
      kind: 'danger',
    });
    if (!stageTwo) return;

    this.registryProjectBusyId.set(e.projectId);
    this.jobService.deleteRegistryProject(e.projectId).subscribe({
      next: (res) => {
        this.registryProjectBusyId.set(null);
        this.reloadRegistryWorkspaces();
        // Host's WatchPaths reload purges stale board/hub tabs for the gone row.
        this.projectDeleted.emit();
        this.notifications.success(`Project "${res?.displayName ?? label}" deleted.`);
      },
      error: (err: unknown) => {
        this.registryProjectBusyId.set(null);
        const msg = this.errMsg(err);
        this.registryWorkspacesError.set(msg);
        this.notifications.error(`Failed to delete project "${label}": ${msg}`);
      },
    });
  }

  /** Drop into the empty trailing region of the tab strip → append. */
  onTabListDragOver(event: DragEvent): void {
    if (!this.draggingTabKey()) return;
    event.preventDefault();
    if (event.dataTransfer) event.dataTransfer.dropEffect = 'move';
    if (this.dragOverTabKey() !== '__end__') {
      this.dragOverTabKey.set('__end__');
    }
  }

  onTabListDrop(event: DragEvent): void {
    event.preventDefault();
    const source = this.draggingTabKey();
    this.draggingTabKey.set(null);
    this.dragOverTabKey.set(null);
    if (!source) return;
    this.tabState.move(source, null);
  }

  /** Right-click context menu state. Coordinates are viewport-relative; the
   *  template positions an absolutely-placed menu at (x, y). One menu at a
   *  time — opening a new one replaces the previous. */
  readonly tabContextMenu = signal<{ key: string; x: number; y: number } | null>(null);

  openTabContextMenu(event: MouseEvent, key: string): void {
    event.preventDefault();
    this.tabContextMenu.set({ key, x: event.clientX, y: event.clientY });
  }

  closeTabContextMenu(): void {
    this.tabContextMenu.set(null);
  }

  // F23: shared <app-menu> driven via pure builders in studio-shell.menu-builders.ts.
  readonly tabCtxMenuItems = computed<readonly MenuItem[]>(() => {
    const ctx = this.tabContextMenu();
    if (!ctx) return [];
    const tabs = this.tabs();
    const idx = tabs.findIndex(t => studioTabKey(t) === ctx.key);
    const tab = idx >= 0 ? tabs[idx] : null;
    let task: { title: string; id: string; key?: string | null } | null = null;
    if (tab && (tab.kind === 'task' || tab.kind === 'activity')) {
      const job = this.findJob(tab.taskKey);
      if (job) task = { title: job.title || job.id, id: job.id, key: job.key };
    }
    return buildTabCtxMenuItems({
      totalTabs: tabs.length,
      hasTabsToRight: idx >= 0 && idx < tabs.length - 1,
      hasTabsToLeft: idx > 0,
      task,
    });
  });
  readonly tabCtxMenuPosition = computed(() => {
    const c = this.tabContextMenu();
    return c ? { x: c.x, y: c.y } : null;
  });
  onTabCtxMenuItemClick(ev: MenuItemClickEvent): void {
    const ctx = this.tabContextMenu();
    if (!ctx) return;
    if (ev.id === 'close') this.closeTab(ctx.key);
    else if (ev.id === 'close-others') this.closeOthers(ctx.key);
    else if (ev.id === 'close-right') this.closeRight(ctx.key);
    else if (ev.id === 'close-left') this.closeLeft(ctx.key);
    else if (ev.id === 'close-all') this.closeAll();
    else if (ev.id === 'copy-name' || ev.id === 'copy-id' || ev.id === 'copy-key') {
      this.handleTabCopyAction(ev.id, ctx.key);
    }
  }

  private handleTabCopyAction(action: string, tabKey: string): void {
    const tabs = this.tabs();
    const tab = tabs.find(t => studioTabKey(t) === tabKey);
    if (!tab || (tab.kind !== 'task' && tab.kind !== 'activity')) return;
    const job = this.findJob(tab.taskKey);
    if (!job) return;
    let text = '';
    let label = '';
    if (action === 'copy-name') { text = job.title || job.id; label = 'Name'; }
    else if (action === 'copy-id') { text = job.id; label = 'ID'; }
    else if (action === 'copy-key' && job.key) { text = job.key; label = 'Key'; }
    if (text) {
      copyTextToClipboard(text).then(ok => {
        if (ok) this.notifications.success(`${label} copied`);
      });
    }
  }
  readonly projectPickerItems = computed<readonly MenuItem[]>(() => buildProjectPickerItems({
    rows: this.projectRows(),
    totalProjectJobs: this.totalProjectJobs(),
    allProjectsActive: this.activeProjectName() === null,
    activeTabKind: this.activeTab()?.kind,
  }));
  onProjectPickerItemClick(ev: MenuItemClickEvent): void {
    this.pickProject(ev.id === '__all__' ? null : ev.id);
  }

  togglePanel(panel: StudioActivityPanelKey | 'settings'): void {
    // The gear ('settings') no longer toggles a sidebar panel — it opens the
    // one consolidated Settings view as an editor tab (AGT-2035).
    if (panel === 'settings') {
      this.toggleWorkspaceSettingsTab();
      return;
    }
    if (panel === 'activity') {
      this.openFeed();
      return;
    }
    this.panelState.toggle(panel);
  }

  /** Open the consolidated Settings view, or close it if it is already the
   *  active editor tab (mirrors the status-bar "Settings" pill toggle). */
  toggleWorkspaceSettingsTab(): void {
    const active = this.tabState.activeTab()?.kind === 'workspace-settings';
    if (active) {
      this.tabState.close(studioTabKey({ kind: 'workspace-settings' }));
      this.workspaceOverlays.close();
      return;
    }
    this.openWorkspaceSettingsTab();
  }

  openWorkspaceSettingsTab(): void {
    this.workspaceOverlays.open(this.workspaceOverlays.section());
    this.tabState.open({ kind: 'workspace-settings' });
  }

  toggleTheme(): void {
    this.themeService.toggle();
  }

  clearAllFilters(): void {
    this.boardFilters.clearAllFilters();
  }

  /**
   * Hook for the "+ Workspace" titlebar button and the "+" icon next to
   * the Workspace group head in the Explorer. Opens the in-app
   * create-workspace dialog (POST /api/workspaces under the hood).
   */
  onAddWorkspace(): void {
    this.workspaceManager.openCreate();
  }

  onAddProjectToWorkspace(workspaceId: string): void {
    this.workspaceManager.openProjectOnboard(workspaceId);
  }

  /** Forces a fresh /api/tasks/grouped pull so the Explorer re-counts. */
  onRefreshWorkspace(): void {
    this.jobService.refresh();
  }

  /** Collapse every workspace, project, Dossier, and Dossier-status branch. */
  onCollapseAllProjects(): void {
    this._expandedProjects.set(new Set<string>());
    this.writeExpandedProjects(new Set<string>());
    this.explorerWorkbenchState.collapseAll(this.projectRows().map(project => project.name));
    this.collapseAllVersion.update(version => version + 1);
  }


  /** Visible CLI types observed across the loaded jobs (for the CLI sidebar panel). */
  readonly cliRows = computed(() => {
    const jobs = this.jobService.jobs();
    const counts = new Map<string, number>();
    for (const j of jobs) {
      const t = j.cliType ?? 'unknown';
      counts.set(t, (counts.get(t) ?? 0) + 1);
    }
    return Array.from(counts.entries())
      .map(([cli, count]) => ({ cli, count, color: cliColorFor(cli) }))
      .sort((a, b) => b.count - a.count);
  });

  startSidebarResize(event: MouseEvent): void {
    event.preventDefault();
    const startX = event.clientX;
    const startW = this.sidebarWidth();
    const onMove = (e: MouseEvent) => {
      this.panelState.setSidebarWidth(startW + (e.clientX - startX));
    };
    const onUp = () => {
      window.removeEventListener('mousemove', onMove);
      window.removeEventListener('mouseup', onUp);
      document.body.style.cursor = '';
      document.body.style.userSelect = '';
    };
    document.body.style.cursor = 'ew-resize';
    document.body.style.userSelect = 'none';
    window.addEventListener('mousemove', onMove);
    window.addEventListener('mouseup', onUp);
  }

  /** Project's registry shortCode (e.g. "ASS") when known, else the full
   *  display name. Keeps project-scoped tab titles short and scannable while
   *  degrading cleanly for projects without a registry shortCode. */
  private projectShortLabel(projectName: string): string {
    return this.projectShortCodeByName().get(projectName) ?? projectName;
  }

  /** Resolve a hub tab's `section` to its rail item (label, icon). A missing
   *  or unknown section falls back to the default rail (`overview`). */
  private railItemForSection(section: string | undefined): ProjectRailItem {
    const key = isProjectRailKey(section) ? section : DEFAULT_PROJECT_RAIL_KEY;
    return (
      PROJECT_RAIL_ITEMS.find(i => i.key === key) ??
      PROJECT_RAIL_ITEMS.find(i => i.key === DEFAULT_PROJECT_RAIL_KEY)!
    );
  }

  /** Map a tab to its displayable label so the template stays terse. */
  tabLabel(tab: StudioTab): string {
    switch (tab.kind) {
      case 'board':
        return tab.projectName === '__all__' ? 'All projects · Board' : `${this.projectShortLabel(tab.projectName)} · Board`;
      case 'feed':
        return 'Activity across projects';
      case 'chat-history':
        return 'Chat History';
      case 'epics':
        return tab.projectName === null ? 'All projects · Epics' : `${this.projectShortLabel(tab.projectName)} · Epics`;
      case 'workbenches':
        return 'Dossiers';
      case 'epic': {
        const labelKey = tab.viewTaskKey ?? tab.epicKey;
        const job = this.findJob(labelKey);
        return job?.title || job?.key || job?.id || this.taskIdFromKey(labelKey);
      }
      case 'task': {
        const job = this.findJob(tab.taskKey);
        return job?.title || job?.key || job?.id || this.taskIdFromKey(tab.taskKey);
      }
      case 'hub': {
        if (tab.section === 'wiki' && tab.wikiTarget && tab.wikiTarget.kind !== 'overview') {
          const pathParts = tab.wikiTarget.relPath.split('/');
          const targetLabel = pathParts[pathParts.length - 1] || tab.wikiTarget.relPath;
          return `${this.projectShortLabel(tab.projectName)} · ${targetLabel}`;
        }
        const railItem = this.railItemForSection(tab.section);
        const surfaceLabel = railItem.key === DEFAULT_PROJECT_RAIL_KEY ? 'Deck' : railItem.label;
        return `${this.projectShortLabel(tab.projectName)} · ${surfaceLabel}`;
      }
      case 'workbench':
        return tab.title || tab.workbenchId;
      case 'diff':
        return tab.commitSha;
      case 'activity': {
        const job = this.findJob(tab.taskKey);
        return `Activity · ${job?.title || job?.key || job?.id || this.taskIdFromKey(tab.taskKey)}`;
      }
      case 'url-preview':
        return this.findProjectUrl(tab.projectName, tab.urlId)?.label || tab.urlId;
      case 'workspace-settings':
        return 'Workspace settings';
      case 'welcome':
        return 'Welcome';
      default:
        return '';
    }
  }

  /** A task key is persisted as `<watchPath>::<jobId>`. During shell restore
   * the tab can render before board data resolves, so its safe fallback must
   * be the user-facing job id rather than the filesystem-bearing key. */
  private taskIdFromKey(taskKey: string): string {
    const separator = taskKey.lastIndexOf('::');
    return separator >= 0 ? taskKey.slice(separator + 2) : taskKey;
  }

  /** Marker for the tab list — used for the small chip on the left
   *  edge of the tab (e.g. `#90` for tasks). The hub / diff / activity
   *  tab labels already include the kind ("· Deck" / commit SHA /
   *  "Activity · …"), so we only render a leading num pill for
   *  task tabs where the `#order` adds info the title doesn't repeat. */
  tabNum(tab: StudioTab): string | null {
    if (tab.kind === 'task') {
      const job = this.findJob(tab.taskKey);
      if (!job) return null;
      return job.key || `#${job.order ?? '?'}`;
    }
    return null;
  }

  /** Leading icon for the tab strip. Deck tabs show their active section's
   *  rail icon (e.g. `book` for Wiki); other kinds render none here (the
   *  epic tab keeps its own dedicated glyph in the template). */
  tabIcon(tab: StudioTab): StudioIconName | null {
    if (tab.kind === 'hub') {
      return this.railItemForSection(tab.section).railIcon ?? null;
    }
    if (tab.kind === 'url-preview') return 'link';
    if (tab.kind === 'chat-history') return 'bot';
    if (tab.kind === 'workbenches') return 'eye';
    if (tab.kind === 'workbench') return 'eye';
    return null;
  }

  /**
   * AGT-2034 — resolve the owning project name for a tab so the tab strip
   * can paint the project's colour dot and keep the full name in the
   * tooltip. Returns `null` for tabs that do not belong to a single project
   * (All-projects board/epics, workspace settings, welcome, diff),
   * so no dot renders there.
   */
  private tabProjectName(tab: StudioTab): string | null {
    switch (tab.kind) {
      case 'board':
        return tab.projectName === '__all__' ? null : tab.projectName;
      case 'epics':
      case 'workbenches':
      case 'hub':
      case 'workbench':
      case 'url-preview':
        return tab.projectName;
      case 'task':
      case 'activity':
        return this.findJob(tab.taskKey)?.projectName ?? null;
      case 'epic':
        return this.findJob(tab.viewTaskKey ?? tab.epicKey)?.projectName ?? null;
      default:
        return null;
    }
  }

  /**
   * AGT-2034 — project-identity colour for the tab's leading dot, or `null`
   * when the tab is not tied to a single project (so no dot renders). Reuses
   * the shared `projectIdentity()` palette — the same hue the Explorer tree
   * and board cards use — so a project's colour reads consistently across the
   * whole shell. No new colour source. The dot only encodes origin; the
   * shortCode (Board/Deck tabs) or task key (Task tabs) beside it stays the
   * primary text label, so colour is never the sole carrier (A11y).
   */
  tabDotColor(tab: StudioTab): string | null {
    const name = this.tabProjectName(tab);
    return name ? projectIdentity(name).color : null;
  }

  /**
   * AGT-2034 — native tooltip text for a tab. The visible label is shortened
   * to `SHORTCODE · Board` to save room; the hover restores the full project
   * name so the origin is never lost. Falls back to the plain label for tabs
   * with no owning project.
   */
  tabTooltip(tab: StudioTab): string {
    const name = this.tabProjectName(tab);
    const label = this.tabLabel(tab);
    if (tab.kind === 'hub' && tab.section === 'wiki'
      && tab.wikiTarget && tab.wikiTarget.kind !== 'overview') {
      return `${name ?? tab.projectName}: ${tab.wikiTarget.relPath}`;
    }
    return name ? `${name} — ${label}` : label;
  }

  private findJob(taskKey: string): TaskInfo | null {
    const grouped = this.grouped();
    for (const lane of Object.values(grouped)) {
      for (const job of lane as TaskInfo[]) {
        if (job.taskKey === taskKey) return job;
      }
    }
    return null;
  }

  /** AGT-2067 — resolve a Project URL record (for the preview tab's label)
   *  from the already-loaded registry workspaces. Returns null until the
   *  registry has loaded or when the URL was removed from the project. */
  private findProjectUrl(projectName: string, urlId: string): RegistryProjectUrl | null {
    for (const ws of this.registryWorkspaces()) {
      for (const p of ws.projects) {
        if (p.displayName !== projectName) continue;
        const url = p.urls?.find(u => u.id === urlId);
        if (url) return url;
      }
    }
    return null;
  }

  /**
   * Map a tab to its underlying TaskInfo so the Open-Tabs hover popover
   * can render a TaskStatusCard. Returns `null` for board / hub / diff /
   * welcome tabs that do not correspond to a single task.
   */
  tabJob(tab: StudioTab): TaskInfo | null {
    if (tab.kind === 'task' || tab.kind === 'activity') return this.findJob(tab.taskKey);
    if (tab.kind === 'epic') return this.findJob(tab.viewTaskKey ?? tab.epicKey);
    return null;
  }
}
