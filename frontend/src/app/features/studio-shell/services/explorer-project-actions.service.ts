import { Injectable, computed, signal } from '@angular/core';
import type { MenuItem } from '@studio/ui/components/menu/index';

/** Open project right-click menu: the targeted row plus the click point. */
export interface ProjectMenuContext {
  projectId: string;
  name: string;
  displayName: string;
  shortCode: string | null;
  x: number;
  y: number;
}

/**
 * F46 — controller state for the Explorer tree's project-row management
 * affordances (right-click menu + inline rename). Extracted from
 * {@link ExplorerWorkspaceTreeComponent} so the presentational component stays
 * within the component-size budget; the component keeps only the DOM concerns
 * (viewChild focus, modal-stack registration) and delegates state here.
 */
@Injectable({ providedIn: 'root' })
export class ExplorerProjectActionsService {
  readonly contextMenu = signal<ProjectMenuContext | null>(null);

  readonly contextMenuItems = computed<readonly MenuItem[]>(() =>
    this.contextMenu()
      ? [
          { kind: 'row', id: 'rename', label: 'Rename' },
          { kind: 'row', id: 'delete', label: 'Delete project…', danger: true },
        ]
      : [],
  );

  readonly contextMenuPosition = computed(() => {
    const c = this.contextMenu();
    return c ? { x: c.x, y: c.y } : null;
  });

  openContextMenu(ctx: ProjectMenuContext): void {
    this.contextMenu.set(ctx);
  }

  closeContextMenu(): void {
    this.contextMenu.set(null);
  }

  /** PROJ id of the row in inline-rename mode (null = none). */
  readonly renamingProjectId = signal<string | null>(null);
  readonly renameDraft = signal('');

  startRename(projectId: string, currentDisplay: string): void {
    this.renameDraft.set(currentDisplay);
    this.renamingProjectId.set(projectId);
  }

  cancelRename(): void {
    this.renamingProjectId.set(null);
  }

  /**
   * Close edit mode and return the rename payload when the draft is a real,
   * non-blank change; null otherwise (blank / unchanged / not editing). Keyed
   * by the stable PROJ id so the row keeps its identity across the rename.
   */
  commitRename(currentDisplay: string): { projectId: string; displayName: string } | null {
    const projectId = this.renamingProjectId();
    if (projectId === null) return null;
    this.renamingProjectId.set(null);
    const value = this.renameDraft().trim();
    if (!value || value === currentDisplay) return null;
    return { projectId, displayName: value };
  }
}
