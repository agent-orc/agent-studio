import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  OnInit,
  computed,
  effect,
  inject,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { CdkDrag, CdkDragDrop, CdkDropList, CdkDropListGroup } from '@angular/cdk/drag-drop';
import { FormsModule } from '@angular/forms';
import { forkJoin } from 'rxjs';
import { TooltipDirective } from 'coding-agent-chat/shared';
import type { RegistryProjectSummary, RegistryWorkspaceListItem } from '../../../../models/task.model';
import { TaskService } from '../../../../services/task.service';
import { ProjectLookupService } from '../../../../services/project-lookup.service';
import { SectionHeaderComponent } from '../../../../components/section-header/section-header.component';
import { MenuComponent, type MenuItem, type MenuItemClickEvent } from '../../../../components/menu';
import { WorkspaceManagerService } from '../../state/workspace-manager.service';
import { WorkspaceSettingsService } from '../../state/workspace-settings.service';
import { WorkspaceQuotaComponent } from '../../../quota/components/workspace-quota/workspace-quota.component';
interface WorkspaceProjectDragData {
  projectId: string;
  sourceWorkspaceId: string | null;
}
interface WorkspaceManagementRow extends RegistryWorkspaceListItem {
  synthetic?: 'unassigned';
}
/**
 * AGT-2035 — Workspace management, extracted from the studio-shell sidebar
 * "Settings" panel into a standalone section of the consolidated
 * Workspace-settings view. Per the operator direction this is a **global**
 * setting: the registry of every workspace + its projects, with rename / color
 * / reorder / delete / project-move / short-code / archive.
 *
 * Self-contained: it owns its own registry list + mutation state and re-pulls
 * on {@link WorkspaceManagerService.registryChanged}, so nothing outside this
 * section has to feed it. Inline-rename state + project drag-and-drop are still
 * delegated to {@link WorkspaceSettingsService} (unchanged from when this lived
 * on the shell).
 */
@Component({
  selector: 'app-workspace-management',
  standalone: true,
  imports: [CdkDrag, CdkDropList, CdkDropListGroup, FormsModule, TooltipDirective, SectionHeaderComponent, MenuComponent, WorkspaceQuotaComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './workspace-management.component.html',
  styleUrl: './workspace-management.component.scss',
})
export class WorkspaceManagementComponent implements OnInit {
  readonly activeQuotaWorkspaceId = signal<string | null>(null);
  private readonly jobService = inject(TaskService);
  private readonly workspaceManager = inject(WorkspaceManagerService);
  private readonly projectLookup = inject(ProjectLookupService);
  readonly wsSettings = inject(WorkspaceSettingsService);
  readonly registryWorkspaces = signal<readonly RegistryWorkspaceListItem[]>([]);
  readonly registryProjects = signal<readonly RegistryProjectSummary[]>([]);
  readonly registryWorkspacesLoading = signal(false);
  readonly registryWorkspacesError = signal<string | null>(null);
  readonly registryWorkspaceBusyId = signal<string | null>(null);
  readonly registryProjectBusyId = signal<string | null>(null);
  readonly projectMoveMenuProjectId = signal<string | null>(null);
  readonly projectMoveMenuSourceWorkspaceId = signal<string | null>(null);
  readonly projectMoveMenuAnchor = signal<HTMLElement | null>(null);
  /** Toggle whether archived projects are shown. Off by default. */
  readonly showArchivedProjects = signal(false);
  readonly workspaceRows = computed<readonly WorkspaceManagementRow[]>(() => {
    const workspaces = this.registryWorkspaces();
    const assignedIds = new Set(workspaces.flatMap(workspace => workspace.projects.map(project => project.id)));
    const unassigned = this.registryProjects().filter(project => !assignedIds.has(project.id));
    if (unassigned.length === 0) return workspaces;
    return [
      ...workspaces,
      {
        id: '__unassigned__',
        displayName: 'Unassigned',
        sortOrder: Number.MAX_SAFE_INTEGER,
        isDefault: false,
        color: null,
        createdAt: '',
        projects: unassigned,
        synthetic: 'unassigned',
      },
    ];
  });
  readonly workspaceEnterPredicate = (
    drag: CdkDrag<WorkspaceProjectDragData>,
    drop: CdkDropList<WorkspaceManagementRow>,
  ): boolean => !this.isUnassignedWorkspace(drop.data)
    && drag.data.sourceWorkspaceId !== drop.data.id;

  readonly projectMoveMenuItems = computed<readonly MenuItem[]>(() => {
    const sourceWorkspaceId = this.projectMoveMenuSourceWorkspaceId();
    if (!this.projectMoveMenuProjectId()) return [];
    const targets = this.registryWorkspaces().filter(workspace => workspace.id !== sourceWorkspaceId);
    if (targets.length === 0) {
      return [
        { kind: 'header', label: 'Move to workspace' },
        { kind: 'row', id: 'no-target', label: 'No other workspace available', disabled: true },
      ];
    }
    return [
      { kind: 'header', label: 'Move to workspace' },
      ...targets.map(workspace => ({
        kind: 'row' as const,
        id: workspace.id,
        label: workspace.displayName,
        hint: workspace.id,
      })),
    ];
  });

  onWorkspaceDrop(
    event: CdkDragDrop<WorkspaceManagementRow, WorkspaceManagementRow, WorkspaceProjectDragData>,
    target: WorkspaceManagementRow,
  ): void {
    this.wsSettings.onProjectDragEnd();
    if (!event.isPointerOverContainer
      || this.isUnassignedWorkspace(target)
      || event.item.data.sourceWorkspaceId === target.id) return;
    this.wsSettings.moveProject(event.item.data.projectId, target.id, target.displayName);
  }

  /** Reload whenever the create-dialog / delete path bumps the counter. */
  private readonly registryChangedFx = effect(() => {
    const rev = this.workspaceManager.registryChanged();
    if (rev === 0) return;
    untracked(() => this.reloadRegistryWorkspaces());
  });

  ngOnInit(): void {
    this.reloadRegistryWorkspaces();
  }

  reloadRegistryWorkspaces(): void {
    this.registryWorkspacesLoading.set(true);
    this.registryWorkspacesError.set(null);
    const includeArchived = this.showArchivedProjects();
    forkJoin({
      workspaces: this.jobService.getRegistryWorkspaces({ includeArchived }),
      projects: this.jobService.getRegistryProjects({ includeArchived }),
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

  onAddWorkspace(): void {
    this.workspaceManager.openCreate();
  }

  /** F45b — prompt for a new workspace name and create it. */
  createRegistryWorkspace(): void {
    const name = window.prompt('New workspace name')?.trim();
    if (!name) return;
    this.jobService.createRegistryWorkspace(name).subscribe({
      next: () => this.reloadRegistryWorkspaces(),
      error: (err: unknown) => this.registryWorkspacesError.set(this.errMsg(err)),
    });
  }

  /** F66 — click-to-edit rename (state owned by WorkspaceSettingsService). */
  renameRegistryWorkspace(ws: RegistryWorkspaceListItem): void {
    this.wsSettings.startRename(ws.id, ws.displayName);
  }

  private readonly renameInputRef = viewChild<ElementRef<HTMLInputElement>>('renameInput');

  private readonly focusRenameInputFx = effect(() => {
    if (this.wsSettings.renamingId() === null) return;
    const el = this.renameInputRef()?.nativeElement;
    if (!el) return;
    queueMicrotask(() => { el.focus(); el.select(); });
  });

  /** F45b — prompt for an accent color hex string. Empty input clears the color. */
  editRegistryWorkspaceColor(ws: RegistryWorkspaceListItem): void {
    const input = window.prompt(
      `Workspace "${ws.displayName}" color (hex like #a78bfa, blank to clear)`,
      ws.color ?? '');
    if (input === null) return;
    const color = input.trim();
    const patch = color ? { color } : { clearColor: true };
    this.registryWorkspaceBusyId.set(ws.id);
    this.jobService.updateRegistryWorkspace(ws.id, patch).subscribe({
      next: () => { this.registryWorkspaceBusyId.set(null); this.reloadRegistryWorkspaces(); },
      error: (err: unknown) => {
        this.registryWorkspaceBusyId.set(null);
        this.registryWorkspacesError.set(this.errMsg(err));
      },
    });
  }

  /** F45b — move a workspace one slot up or down. */
  moveRegistryWorkspace(ws: RegistryWorkspaceListItem, direction: -1 | 1): void {
    this.registryWorkspaceBusyId.set(ws.id);
    this.jobService.reorderRegistryWorkspace(ws.id, direction).subscribe({
      next: () => { this.registryWorkspaceBusyId.set(null); this.reloadRegistryWorkspaces(); },
      error: (err: unknown) => {
        this.registryWorkspaceBusyId.set(null);
        this.registryWorkspacesError.set(this.errMsg(err));
      },
    });
  }

  /**
   * F66 — delete a workspace after a confirm dialog. Only allowed once the
   * workspace is empty (ADR-0048: operator moves projects out first).
   */
  deleteRegistryWorkspace(ws: RegistryWorkspaceListItem): void {
    if (!this.canDeleteWorkspace(ws)) return;
    const ok = window.confirm(`Delete workspace "${ws.displayName}"?`);
    if (!ok) return;
    this.registryWorkspaceBusyId.set(ws.id);
    this.jobService.deleteRegistryWorkspace(ws.id).subscribe({
      next: () => { this.registryWorkspaceBusyId.set(null); this.reloadRegistryWorkspaces(); },
      error: (err: unknown) => {
        this.registryWorkspaceBusyId.set(null);
        this.registryWorkspacesError.set(this.errMsg(err));
      },
    });
  }

  canDeleteWorkspace(ws: RegistryWorkspaceListItem): boolean {
    return !ws.isDefault && ws.projects.length === 0;
  }

  isUnassignedWorkspace(ws: WorkspaceManagementRow): boolean {
    return ws.synthetic === 'unassigned';
  }

  workspaceDeleteTooltip(ws: RegistryWorkspaceListItem): string {
    if (ws.isDefault) return 'Default workspace cannot be deleted';
    const count = ws.projects.length;
    if (count > 0) {
      return `Move all ${count} project${count === 1 ? '' : 's'} out of this workspace before it can be deleted.`;
    }
    return 'Delete this workspace';
  }

  canMoveWorkspaceUp(ws: RegistryWorkspaceListItem): boolean {
    const list = this.registryWorkspaces();
    return list.length > 0 && list[0].id !== ws.id;
  }

  canMoveWorkspaceDown(ws: RegistryWorkspaceListItem): boolean {
    const list = this.registryWorkspaces();
    return list.length > 0 && list[list.length - 1].id !== ws.id;
  }

  /** F45b — rename a project by prompt. */
  renameRegistryProject(projId: string, currentDisplayName: string): void {
    const name = window.prompt(`Rename project "${currentDisplayName}"`, currentDisplayName)?.trim();
    if (!name || name === currentDisplayName) return;
    this.runProjectPatch(projId, { displayName: name });
  }

  /** F45b — change the short code (2-6 chars A-Z 0-9). */
  editRegistryProjectShortCode(projId: string, currentShortCode: string): void {
    const code = window.prompt(
      `Project ${projId} short code (2-6 chars, A-Z and 0-9)`, currentShortCode)?.trim();
    if (!code || code.toUpperCase() === currentShortCode) return;
    this.runProjectPatch(projId, { shortCode: code });
  }

  /** F45b — set or clear the project color. */
  editRegistryProjectColor(projId: string, currentColor: string | null): void {
    const input = window.prompt(
      `Project ${projId} color (hex like #a78bfa, blank to clear)`, currentColor ?? '');
    if (input === null) return;
    const color = input.trim();
    this.runProjectPatch(projId, color ? { color } : { clearColor: true });
  }

  /** Opens the keyboard-accessible workspace picker for the non-drag move path. */
  openProjectMoveMenu(event: MouseEvent, projectId: string, sourceWorkspaceId: string | null): void {
    event.stopPropagation();
    this.projectMoveMenuProjectId.set(projectId);
    this.projectMoveMenuSourceWorkspaceId.set(sourceWorkspaceId);
    this.projectMoveMenuAnchor.set(event.currentTarget as HTMLElement);
  }

  closeProjectMoveMenu(): void {
    this.projectMoveMenuProjectId.set(null);
    this.projectMoveMenuSourceWorkspaceId.set(null);
    this.projectMoveMenuAnchor.set(null);
  }

  onProjectMoveMenuItemClick(event: MenuItemClickEvent): void {
    const projectId = this.projectMoveMenuProjectId();
    const sourceWorkspaceId = this.projectMoveMenuSourceWorkspaceId();
    const target = this.registryWorkspaces().find(workspace => workspace.id === event.id);
    this.closeProjectMoveMenu();
    if (!projectId || !target || target.id === sourceWorkspaceId) return;
    this.wsSettings.moveProject(projectId, target.id, target.displayName);
  }

  isProjectBusy(projectId: string): boolean {
    return this.registryProjectBusyId() === projectId || this.wsSettings.movingProjectId() === projectId;
  }

  /** F45b — archive (or un-archive) a project. */
  toggleRegistryProjectArchived(projId: string, archived: boolean): void {
    const verb = archived ? 'Un-archive' : 'Archive';
    const ok = window.confirm(`${verb} project ${projId}? Archived projects are hidden from the tree by default.`);
    if (!ok) return;
    this.runProjectPatch(projId, { archived: !archived });
  }

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

  toggleShowArchivedProjects(): void {
    this.showArchivedProjects.update(v => !v);
    this.reloadRegistryWorkspaces();
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
}
