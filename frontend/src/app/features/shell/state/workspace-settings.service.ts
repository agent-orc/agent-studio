import { Injectable, computed, inject, signal } from '@angular/core';
import { TaskService } from '../../../services/task.service';
import { NotificationService } from '@studio/ui/services/notification.service';
import { WorkspaceManagerService } from './workspace-manager.service';
import { ProjectLifecycleDialogService } from '../../../services/project-lifecycle-dialog.service';
import type { RegistryProjectSummary } from '../../../models/task.model';

/**
 * F66 / ADR-0048 — owns the Settings panel's two workspace-management
 * interactions that previously lived inline on the studio shell:
 *
 * <ul>
 *   <li>Inline-edit rename: clicking a workspace name flips the row into
 *       an editable input. Enter and blur persist, Escape cancels. The
 *       input is empty-trim-rejected; backend duplicate / validation
 *       errors surface via the shared error signal.</li>
 *   <li>Project → workspace drag-and-drop: drag a project row, drop it
 *       on another workspace row, the project's `workspaceId` is patched
 *       via `PUT /api/projects/{id}`. Workspace and project storage on
 *       disk is not touched (ADR-0048: workspaces are virtual).</li>
 * </ul>
 *
 * After every successful mutation the service bumps
 * `WorkspaceManagerService.registryChanged` so the studio shell's
 * effect re-fetches the workspace list with the same code path as the
 * create / delete flows.
 */
@Injectable({ providedIn: 'root' })
export class WorkspaceSettingsService {
  private readonly jobService = inject(TaskService);
  private readonly notifications = inject(NotificationService);
  private readonly manager = inject(WorkspaceManagerService);
  private readonly lifecycleDialogs = inject(ProjectLifecycleDialogService);
  readonly deletingProjectId = signal<string | null>(null);
  readonly deleteError = signal<string | null>(null);

  async deleteProject(project: RegistryProjectSummary): Promise<void> {
    if (!await this.lifecycleDialogs.confirmDelete({
      projectId: project.id, displayName: project.displayName, shortCode: project.shortCode,
    })) return;
    this.deletingProjectId.set(project.id);
    this.deleteError.set(null);
    this.jobService.deleteRegistryProject(project.id).subscribe({
      next: () => {
        this.deletingProjectId.set(null);
        this.manager.refreshAfterDelete();
        this.notifications.success(`Project "${project.displayName}" deleted.`);
      },
      error: (err: unknown) => {
        this.deletingProjectId.set(null);
        const message = this.errMsg(err);
        this.deleteError.set(message);
        this.notifications.error(`Failed to delete project "${project.displayName}": ${message}`);
      },
    });
  }

  // ---- Inline rename state ----
  private readonly _renamingId = signal<string | null>(null);
  private readonly _renameDraft = signal<string>('');
  private readonly _renameBusy = signal(false);
  private readonly _renameError = signal<string | null>(null);

  readonly renamingId = this._renamingId.asReadonly();
  readonly renameDraft = this._renameDraft.asReadonly();
  readonly renameBusy = this._renameBusy.asReadonly();
  readonly renameError = this._renameError.asReadonly();

  isRenaming(workspaceId: string): boolean {
    return this._renamingId() === workspaceId;
  }

  startRename(workspaceId: string, currentDisplayName: string): void {
    this._renameError.set(null);
    this._renamingId.set(workspaceId);
    this._renameDraft.set(currentDisplayName);
  }

  updateRenameDraft(value: string): void {
    this._renameDraft.set(value);
  }

  cancelRename(): void {
    if (this._renameBusy()) return;
    this._renamingId.set(null);
    this._renameDraft.set('');
    this._renameError.set(null);
  }

  /**
   * Commit the inline rename. Empty / whitespace input cancels (matches
   * the old window.prompt semantics where the operator dismissed the
   * dialog). No-op when the draft equals the original display name.
   * Backend rejection surfaces via `renameError` so the row can show it
   * without losing the user's typed text.
   */
  commitRename(currentDisplayName: string): void {
    const id = this._renamingId();
    if (!id) return;
    if (this._renameBusy()) return;
    const next = this._renameDraft().trim();
    if (!next) { this.cancelRename(); return; }
    if (next === currentDisplayName) { this.cancelRename(); return; }
    this._renameBusy.set(true);
    this._renameError.set(null);
    this.jobService.updateRegistryWorkspace(id, { displayName: next }).subscribe({
      next: () => {
        this._renameBusy.set(false);
        this._renamingId.set(null);
        this._renameDraft.set('');
        this.manager.refreshAfterDelete();
        this.notifications.success(`Renamed workspace to "${next}".`);
      },
      error: (err: unknown) => {
        this._renameBusy.set(false);
        this._renameError.set(this.errMsg(err));
      },
    });
  }

  // ---- Project → workspace drag-and-drop state ----

  private readonly _draggingProjectId = signal<string | null>(null);
  private readonly _draggingFromWorkspaceId = signal<string | null>(null);
  private readonly _dragOverWorkspaceId = signal<string | null>(null);
  private readonly _movingProjectId = signal<string | null>(null);
  private readonly _dropError = signal<string | null>(null);

  readonly draggingProjectId = this._draggingProjectId.asReadonly();
  readonly dragOverWorkspaceId = this._dragOverWorkspaceId.asReadonly();
  readonly movingProjectId = this._movingProjectId.asReadonly();
  readonly dropBusy = computed(() => this._movingProjectId() !== null);
  readonly dropError = this._dropError.asReadonly();

  readonly isDragging = computed(() => this._draggingProjectId() !== null);

  isDragOver(workspaceId: string): boolean {
    return this._dragOverWorkspaceId() === workspaceId;
  }

  onProjectDragStart(projectId: string, sourceWorkspaceId: string | null): void {
    this._draggingProjectId.set(projectId);
    this._draggingFromWorkspaceId.set(sourceWorkspaceId);
    this._dropError.set(null);
  }

  canDropOnWorkspace(targetWorkspaceId: string): boolean {
    const sourceWs = this._draggingFromWorkspaceId();
    return this._draggingProjectId() !== null && sourceWs !== targetWorkspaceId;
  }

  onWorkspaceDragEnter(targetWorkspaceId: string): void {
    if (!this.canDropOnWorkspace(targetWorkspaceId)) return;
    if (this._dragOverWorkspaceId() !== targetWorkspaceId) {
      this._dragOverWorkspaceId.set(targetWorkspaceId);
    }
  }

  onWorkspaceDragLeave(workspaceId: string): void {
    if (this._dragOverWorkspaceId() === workspaceId) {
      this._dragOverWorkspaceId.set(null);
    }
  }

  onProjectDragEnd(): void {
    this._draggingProjectId.set(null);
    this._draggingFromWorkspaceId.set(null);
    this._dragOverWorkspaceId.set(null);
  }

  moveProject(projectId: string, targetWorkspaceId: string, targetDisplayName: string): void {
    if (this._movingProjectId()) return;
    this._movingProjectId.set(projectId);
    this._dropError.set(null);
    this.jobService.updateRegistryProject(projectId, { workspaceId: targetWorkspaceId }).subscribe({
      next: () => {
        this._movingProjectId.set(null);
        this.manager.refreshAfterDelete();
        this.notifications.success(`Moved project to "${targetDisplayName}".`);
      },
      error: (err: unknown) => {
        this._movingProjectId.set(null);
        this._dropError.set(this.errMsg(err));
        this.notifications.error(`Could not move project: ${this.errMsg(err)}`);
      },
    });
  }

  private errMsg(err: unknown): string {
    if (err == null) return 'Unknown error';
    if (typeof err === 'string') return err;
    const anyErr = err as { error?: { error?: string }; message?: string };
    return anyErr.error?.error ?? anyErr.message ?? String(err);
  }
}
