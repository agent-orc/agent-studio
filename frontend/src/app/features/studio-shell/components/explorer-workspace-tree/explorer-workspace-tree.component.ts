import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  ViewEncapsulation,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { CdkDrag, CdkDragDrop, CdkDropList, CdkDropListGroup } from '@angular/cdk/drag-drop';
import type {
  RegistryProjectSummary,
  RegistryProjectUrl,
  RegistryWorkspaceListItem,
} from '../../../../models/task.model';
import { ProjectUrlProbeService } from '../../../../services/project-url-probe.service';
import { ModalStackService } from '../../../../services/modal-stack.service';
import { StudioIconComponent } from '../../../../components/studio-icon/studio-icon.component';
import { EmptyStateComponent } from '../../../../components/empty-state/empty-state.component';
import { SectionHeaderComponent } from '../../../../components/section-header/section-header.component';
import { TreeRowComponent } from '../../../../components/tree-row/tree-row.component';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { MenuComponent, type MenuItem, type MenuItemClickEvent } from '../../../../components/menu';
import { ProjectDragDropService } from '../../../shell/shell-api';
import { ExplorerSectionsService } from '../../services/explorer-sections.service';
import { ExplorerProjectActionsService } from '../../services/explorer-project-actions.service';
import { boardLaneCountsLabel, laneCountsFor } from '../../studio-shell.project-rows';
import { ExplorerLaneDashboardComponent, type ExplorerTreeMetricView } from '../explorer-lane-dashboard/explorer-lane-dashboard.component';
import {
  aggregateAutoPickup,
  aggregateAutoPickupTooltip,
  type ExplorerAutoPickupAggregate,
  type ProjectAutoPickupIndicator,
} from '../../studio-shell.auto-pickup';
import { ExplorerAutoPickupIndicatorComponent } from '../explorer-auto-pickup-indicator/explorer-auto-pickup-indicator.component';
import type { WorkbenchListItem } from '../../../../models/project-docs.model';
import { ExplorerWorkbenchListComponent } from '../explorer-workbench-list/explorer-workbench-list.component';
import {
  buildExplorerWorkspaceGroups,
  type ExplorerProjectNode,
  type ExplorerProjectRow,
  type ExplorerWorkspaceGroup,
} from './explorer-workspace-groups';
export type {
  ExplorerProjectNode,
  ExplorerProjectRow,
  ExplorerWorkspaceGroup,
} from './explorer-workspace-groups';

export type ExplorerProjectSurface = 'board' | 'hub' | 'wiki' | 'workbenches' | 'workbench' | 'epics';
export interface ActiveExplorerWorkbench {
  projectName: string;
  workbenchId: string;
}

/**
 * F46 — Explorer two-level workspace → project tree. Purely presentational
 * over the shell's flat project rows, joined to
 * {@link RegistryWorkspaceListItem}s by storage path / display name / folder
 * tail; unmatched rows fall into "Unassigned", and an empty registry falls
 * back to one legacy folder so the tree never goes blank. In-tree management
 * affordances (workspace + project rename, project delete) are surfaced at the
 * node via double-click / right-click; all edits are registry-only.
 */
@Component({
  selector: 'app-explorer-workspace-tree',
  standalone: true,
  imports: [CdkDrag, CdkDropList, CdkDropListGroup, SectionHeaderComponent, TreeRowComponent, StudioIconComponent, EmptyStateComponent, TooltipDirective, MenuComponent, ExplorerAutoPickupIndicatorComponent, ExplorerLaneDashboardComponent, ExplorerWorkbenchListComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  encapsulation: ViewEncapsulation.None,
  templateUrl: './explorer-workspace-tree.component.html',
  styleUrl: './explorer-workspace-tree.component.scss',
})
export class ExplorerWorkspaceTreeComponent {
  readonly projectRows = input<readonly ExplorerProjectRow[]>([]);
  readonly registryWorkspaces = input<readonly RegistryWorkspaceListItem[]>([]);
  /** Flat registry list also contains records whose workspaceId is empty or
   *  invalid, which GET /workspaces cannot embed under a real workspace. */
  readonly registryProjects = input<readonly RegistryProjectSummary[]>([]);
  /** Row name → resolved storage path (from the host's WatchPaths). Lets the
   *  workspace→project join key on storage instead of the mutable display
   *  name, so a registry rename keeps the row under its workspace (F46 step 7). */
  readonly projectStorageByName = input<ReadonlyMap<string, string>>(new Map());
  readonly expandedProjects = input<ReadonlySet<string>>(new Set());
  readonly showAllActive = input(false);
  readonly activeProjectSurface = input<ExplorerProjectSurface | null>(null);
  readonly activeWorkbench = input<ActiveExplorerWorkbench | null>(null);
  /** Monotonic command from the tree head. Each new value closes every
   * workspace branch without becoming a reactive default for later clicks. */
  readonly collapseAllVersion = input(0);
  /** Experimental active-work visualization. Numbers remain the default. */
  readonly metricView = input<ExplorerTreeMetricView>('numbers');
  /** Project name to always-visible auto-pickup configuration and gate state. */
  readonly projectAutoPickupByName = input<ReadonlyMap<string, ProjectAutoPickupIndicator>>(new Map());

  readonly showAll = output<void>();
  readonly toggleExpanded = output<string>();
  readonly openBoardRequest = output<string>();
  readonly openHubRequest = output<string>();
  /** AGT-2067 — open a URL row's embedded preview tab (project + url id). */
  readonly openUrlPreviewRequest = output<{ projectName: string; urlId: string }>();
  readonly openWikiRequest = output<string>();
  readonly openWorkbenchRequest = output<{ projectName: string; workbench: WorkbenchListItem }>();
  readonly openWorkbenchesRequest = output<string>();
  readonly openEpicsRequest = output<string>();
  readonly onboardProjectRequest = output<string>();
  /** Open the create-workspace dialog from the Workspaces section header. */
  readonly onboardWorkspaceRequest = output<void>();
  /** Project row dropped onto a different real workspace; the shell PUTs
   *  /api/projects/{projectId} `{ workspaceId }` and reloads (no folder move). */
  readonly projectDrop = output<{ projectId: string; targetWorkspaceId: string }>();

  /** F46 — workspace-header inline rename committed from the editor; the shell
   *  PUTs /api/workspaces/{id} (registry metadata only, no folder move). */
  readonly renameWorkspace = output<{ id: string; displayName: string }>();

  /** F46 step 7 — registry-only project rename committed from the inline editor
   *  (shell PUTs `{ displayName }`; PROJ id + storage untouched). */
  readonly renameProject = output<{ projectId: string; displayName: string }>();

  /** F46 — destructive project delete from the right-click menu; the shell runs
   *  the two-stage typed confirm + DELETE. `shortCode` lets the confirm accept
   *  it as an alias for the display name. */
  readonly deleteProject = output<{ projectId: string; displayName: string; shortCode: string | null }>();

  readonly projectDrag = inject(ProjectDragDropService);
  readonly projectActions = inject(ExplorerProjectActionsService);
  /** Public so the template can read each URL row's live running/offline dot. */
  readonly urlProbe = inject(ProjectUrlProbeService);
  private readonly sections = inject(ExplorerSectionsService);
  private readonly modalStack = inject(ModalStackService);

  /** Id of the workspace header currently in inline-rename mode (null = none). */
  readonly renamingWsId = signal<string | null>(null);
  readonly renameDraft = signal('');

  private readonly renameInputRef = viewChild<ElementRef<HTMLInputElement>>('wsRenameInput');
  private readonly focusRenameFx = effect(() => {
    if (this.renamingWsId() === null) return;
    const el = this.renameInputRef()?.nativeElement;
    if (el) queueMicrotask(() => { el.focus(); el.select(); });
  });

  // Push the open rename input onto the modal stack so Escape cancels it:
  // always-mounted overlays keep the stack non-empty, so the input's own
  // (keydown) Escape would otherwise be swallowed by the stack top first.
  private renameModalDispose: (() => void) | null = null;
  private readonly renameModalFx = effect(() => {
    const renaming = this.renamingWsId() !== null;
    if (renaming && !this.renameModalDispose) {
      this.renameModalDispose = this.modalStack.push('explorer-ws-rename', () => {
        this.cancelRename();
        return true;
      });
    } else if (!renaming && this.renameModalDispose) {
      this.renameModalDispose();
      this.renameModalDispose = null;
    }
  });

  private readonly projectRenameInputRef = viewChild<ElementRef<HTMLInputElement>>('projectRenameInput');
  private readonly focusProjectRenameFx = effect(() => {
    if (this.projectActions.renamingProjectId() === null) return;
    const el = this.projectRenameInputRef()?.nativeElement;
    if (el) queueMicrotask(() => { el.focus(); el.select(); });
  });

  // Project-row rename: same modal-stack Escape arbitration as the workspace one.
  private projectRenameModalDispose: (() => void) | null = null;
  private readonly projectRenameModalFx = effect(() => {
    const renaming = this.projectActions.renamingProjectId() !== null;
    if (renaming && !this.projectRenameModalDispose) {
      this.projectRenameModalDispose = this.modalStack.push('explorer-project-rename', () => {
        this.projectActions.cancelRename();
        return true;
      });
    } else if (!renaming && this.projectRenameModalDispose) {
      this.projectRenameModalDispose();
      this.projectRenameModalDispose = null;
    }
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.renameModalDispose?.();
      this.projectRenameModalDispose?.();
    });
  }

  readonly totalProjectCount = computed(() => this.projectRows().length);


  readonly groups = computed<ExplorerWorkspaceGroup[]>(() =>
    buildExplorerWorkspaceGroups(
      this.projectRows(),
      this.registryWorkspaces(),
      this.registryProjects(),
      this.projectStorageByName(),
    ));

  private lastRevealedNavigationPath: string | null = null;
  private readonly revealActiveNavigationFx = effect(() => {
    const activeWorkbench = this.activeWorkbench();
    const activeSurface = this.activeProjectSurface();
    const projectName = activeWorkbench?.projectName
      ?? this.groups().flatMap(group => group.projects).find(project => project.isActive)?.name;
    if (!projectName || !activeSurface) {
      this.lastRevealedNavigationPath = null;
      return;
    }

    const workspace = this.groups()
      .find(group => group.projects.some(project => project.name === projectName));
    if (!workspace) return;

    const revealPath = `${workspace.id}:${projectName}:${activeSurface}:${activeWorkbench?.workbenchId ?? ''}`;
    if (this.lastRevealedNavigationPath === revealPath) return;
    this.lastRevealedNavigationPath = revealPath;
    this.setCollapsed('workspace', false);
    this.setCollapsed(`ws:${workspace.id}`, false);
  });

  private lastCollapseAllVersion = 0;
  private readonly collapseAllFx = effect(() => {
    const version = this.collapseAllVersion();
    const groups = this.groups();
    if (version === 0 || version === this.lastCollapseAllVersion) return;
    this.lastCollapseAllVersion = version;
    this.setCollapsed('workspace', true);
    for (const group of groups) this.setCollapsed(`ws:${group.id}`, true);
  });

  isCollapsed(key: string): boolean {
    return this.sections.isCollapsed(key);
  }

  setCollapsed(key: string, collapsed: boolean): void {
    this.sections.setCollapsed(key, collapsed);
  }

  // Toggle from the live service value, not the row's [chevron] input: a
  // double-click fires two clicks before the row re-renders, so both would read
  // the same stale value and collapse instead of netting back.
  onWsHeaderToggle(g: ExplorerWorkspaceGroup): void {
    const key = 'ws:' + g.id;
    this.setCollapsed(key, !this.isCollapsed(key));
  }

  isExpanded(name: string): boolean {
    return this.expandedProjects().has(name);
  }

  activeWorkbenchIdFor(projectName: string): string | null {
    const activeWorkbench = this.activeWorkbench();
    return activeWorkbench?.projectName === projectName ? activeWorkbench.workbenchId : null;
  }

  /** AGT-2067 — primary click on a URL row opens its embedded preview tab. */
  openUrlPreview(projectName: string, urlId: string): void {
    this.openUrlPreviewRequest.emit({ projectName, urlId });
  }

  /** Fallback escape hatch kept on the row: open the URL in a real browser tab. */
  openUrlExternal(url: string, event?: Event): void {
    event?.stopPropagation();
    window.open(url, '_blank', 'noopener');
  }

  readonly laneCountsFor = laneCountsFor;
  readonly boardLaneCountsLabel = boardLaneCountsLabel;

  autoPickupFor(name: string): ProjectAutoPickupIndicator {
    return this.projectAutoPickupByName().get(name) ?? {
      state: 'manual',
      reason: null,
      tooltip: 'Auto-pickup manual',
    };
  }

  readonly aggregateAutoPickupTooltip = aggregateAutoPickupTooltip;

  /** Roll active and blocked auto-continuous projects into one workspace mark. */
  wsAutoPickupAggregate(g: ExplorerWorkspaceGroup): ExplorerAutoPickupAggregate {
    return aggregateAutoPickup(g.projects.map(p => p.name), this.projectAutoPickupByName());
  }

  /** Whole-tree aggregate, shown on the panel header when the tree is collapsed. */
  readonly allAutoPickupAggregate = computed<ExplorerAutoPickupAggregate>(() => {
    const indicators = this.projectAutoPickupByName();
    return aggregateAutoPickup([...indicators.keys()], indicators);
  });

  /** Enter inline-rename for a real workspace header (synthetic groups no-op). */
  startRenameWorkspace(g: ExplorerWorkspaceGroup): void {
    if (g.id === '__all__' || g.id === '__unassigned__') return;
    this.renameDraft.set(g.displayName);
    this.renamingWsId.set(g.id);
  }

  /** Right-click "Rename" menu on a workspace header (viewport coords for
   *  `<app-menu>`); synthetic groups fall through to the native menu. */
  readonly wsContextMenu = signal<{ id: string; x: number; y: number } | null>(null);

  readonly wsContextMenuItems = computed<readonly MenuItem[]>(() =>
    this.wsContextMenu() ? [{ kind: 'row', id: 'rename', label: 'Rename' }] : [],
  );

  readonly wsContextMenuPosition = computed(() => {
    const ctx = this.wsContextMenu();
    return ctx ? { x: ctx.x, y: ctx.y } : null;
  });

  openWsContextMenu(event: MouseEvent, g: ExplorerWorkspaceGroup): void {
    if (g.id === '__all__' || g.id === '__unassigned__') return;
    event.preventDefault();
    this.wsContextMenu.set({ id: g.id, x: event.clientX, y: event.clientY });
  }

  closeWsContextMenu(): void {
    this.wsContextMenu.set(null);
  }

  onWsContextMenuItemClick(ev: MenuItemClickEvent): void {
    const ctx = this.wsContextMenu();
    this.closeWsContextMenu();
    if (!ctx || ev.id !== 'rename') return;
    const g = this.groups().find(group => group.id === ctx.id);
    if (g) this.startRenameWorkspace(g);
  }

  /** Right-click menu on a project row; only registry-backed rows (PROJ id)
   *  get it, unmatched rows fall through to the native menu. */
  onProjectContextMenu(event: MouseEvent, p: ExplorerProjectNode): void {
    if (!p.projectId) return;
    event.preventDefault();
    event.stopPropagation();
    this.projectActions.openContextMenu({
      projectId: p.projectId,
      name: p.name,
      displayName: p.displayLabel,
      shortCode: p.shortCode,
      x: event.clientX,
      y: event.clientY,
    });
  }

  onProjectContextMenuItemClick(ev: MenuItemClickEvent): void {
    const ctx = this.projectActions.contextMenu();
    this.projectActions.closeContextMenu();
    if (!ctx) return;
    if (ev.id === 'rename') {
      this.projectActions.startRename(ctx.projectId, ctx.displayName);
    } else if (ev.id === 'delete') {
      this.deleteProject.emit({ projectId: ctx.projectId, displayName: ctx.displayName, shortCode: ctx.shortCode });
    }
  }

  onProjectRenameKeydown(ev: KeyboardEvent): void {
    if (ev.key === 'Enter') {
      ev.preventDefault();
      this.commitRenameProject();
    } else if (ev.key === 'Escape') {
      ev.preventDefault();
      this.projectActions.cancelRename();
    }
  }

  /** Commit the inline rename, emitting {@link renameProject} on a real change. */
  commitRenameProject(): void {
    const projectId = this.projectActions.renamingProjectId();
    if (projectId === null) return;
    const current = this.findProjectNode(projectId)?.displayLabel ?? '';
    const result = this.projectActions.commitRename(current);
    if (result) this.renameProject.emit(result);
  }

  private findProjectNode(projectId: string): ExplorerProjectNode | undefined {
    for (const g of this.groups()) {
      const found = g.projects.find(p => p.projectId === projectId);
      if (found) return found;
    }
    return undefined;
  }

  cancelRename(): void {
    this.renamingWsId.set(null);
  }

  // Raw keydown (not keydown.enter/.escape pseudo-events: the Escape filter
  // did not fire reliably here). Enter commits, Escape cancels.
  onRenameKeydown(ev: KeyboardEvent): void {
    if (ev.key === 'Enter') {
      ev.preventDefault();
      this.commitRename();
    } else if (ev.key === 'Escape') {
      ev.preventDefault();
      this.cancelRename();
    }
  }

  /** Commit the draft name (no-op when blank/unchanged); closing edit mode
   *  first makes the blur that follows Enter/Escape a no-op. */
  commitRename(): void {
    const id = this.renamingWsId();
    if (id === null) return;
    this.renamingWsId.set(null);
    const value = this.renameDraft().trim();
    const original = this.groups().find(g => g.id === id)?.displayName ?? '';
    if (!value || value === original) return;
    this.renameWorkspace.emit({ id, displayName: value });
  }

  canDropOnWorkspace(targetWorkspaceId: string): boolean {
    return this.projectDrag.canDropOnWorkspace(targetWorkspaceId);
  }

  readonly workspaceEnterPredicate = (
    drag: CdkDrag<ExplorerProjectNode>,
    drop: CdkDropList<ExplorerWorkspaceGroup>,
  ): boolean => this.projectDrag.canMoveProjectToWorkspace(drag.data, drop.data.id);

  projectTooltip(p: ExplorerProjectNode): string {
    const guidance = p.projectId
      ? 'Drag onto a workspace folder to move this project there'
      : 'Not registered. Use + on the destination workspace to onboard this project before moving it.';
    const openTasks = p.totalJobs === 1 ? '1 open task' : `${p.totalJobs} open tasks`;
    return [p.displayLabel, openTasks, this.autoPickupFor(p.name).tooltip, guidance].join('\n');
  }

  urlTooltip(p: ExplorerProjectNode, url: RegistryProjectUrl): string {
    const status = this.urlProbe.statusFor(p.projectId ?? '', url.id);
    const statusLabel = status === 'running' ? 'Running' : status === 'offline' ? 'Offline' : 'Status unknown';
    return [url.label, url.url, statusLabel].join('\n');
  }
  onDragStart(p: ExplorerProjectNode): void {
    this.projectDrag.onDragStart({
      projectId: p.projectId,
      name: p.name,
      workspaceId: p.workspaceId,
    });
  }

  onWorkspaceDragEnter(g: ExplorerWorkspaceGroup): void {
    this.projectDrag.onWorkspaceDragEnter(g.id);
  }

  onWorkspaceDragLeave(g: ExplorerWorkspaceGroup): void {
    this.projectDrag.onWorkspaceDragLeave(g.id);
  }

  onWorkspaceDrop(
    event: CdkDragDrop<ExplorerWorkspaceGroup, ExplorerWorkspaceGroup, ExplorerProjectNode>,
    g: ExplorerWorkspaceGroup,
  ): void {
    const projectId = event.item.data.projectId;
    const valid = this.projectDrag.canMoveProjectToWorkspace(event.item.data, g.id);
    this.projectDrag.onDragEnd();
    if (valid && projectId) {
      this.projectDrop.emit({ projectId, targetWorkspaceId: g.id });
    }
  }

  onDragEnd(): void {
    this.projectDrag.onDragEnd();
  }
}
