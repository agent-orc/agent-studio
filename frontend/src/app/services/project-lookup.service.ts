import { Injectable, computed, signal } from '@angular/core';
import type { RegistryWorkspaceListItem } from '../models/task.model';
import { projectIdentity } from './project-identity.util';

export interface ProjectDisplay {
  id: string | null;
  displayName: string;
  shortCode: string | null;
  color: string;
  initial: string;
  onColor: string;
  border: string;
  soft: string;
}

function normalizeStorage(path: string): string {
  return path.replace(/[\\/]+/g, '/').replace(/\/+$/, '').toLowerCase();
}

@Injectable({ providedIn: 'root' })
export class ProjectLookupService {
  private readonly workspaces = signal<readonly RegistryWorkspaceListItem[]>([]);

  readonly allProjects = computed(() => this.workspaces().flatMap(ws => ws.projects));
  /** True once any registry response arrived, so an empty list means "no projects", not "not loaded". */
  readonly loaded = signal(false);

  setWorkspaces(workspaces: readonly RegistryWorkspaceListItem[]): void {
    this.workspaces.set(workspaces ?? []);
    this.loaded.set(true);
  }

  getProjectDisplay(projectName: string, storagePath?: string | null, projectId?: string | null): ProjectDisplay {
    const normalized = storagePath ? normalizeStorage(storagePath) : null;
    const match = this.allProjects().find(p =>
      (!!projectId && p.id === projectId) ||
      p.displayName === projectName ||
      (!!normalized && normalizeStorage(p.storageLocation) === normalized)
    );
    const label = match?.displayName ?? projectName;
    const identity = projectIdentity(label);
    return {
      id: match?.id ?? null,
      displayName: label,
      shortCode: match?.shortCode ?? null,
      color: match?.color ?? identity.color,
      initial: identity.initial,
      onColor: identity.onColor,
      border: identity.border,
      soft: identity.soft,
    };
  }
}
