import { Injectable, computed, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { TagRegistryEntry } from '../models/task.model';

/**
 * Workspace tags plus the effective registry for the active project. A
 * project response never changes another project's available filters.
 */
@Injectable({ providedIn: 'root' })
export class TagRegistryStore {
  private readonly http = inject(HttpClient);
  readonly workspaceTags = signal<TagRegistryEntry[]>([]);
  private workspaceLoaded = false;
  private readonly projectTags = signal(new Map<string, TagRegistryEntry[]>());
  private readonly pendingProjects = new Set<string>();
  readonly activeProject = signal<string | null>(null);
  readonly activeProjectLoaded = signal(false);
  readonly tags = signal<TagRegistryEntry[]>([]);
  readonly byId = computed(() => {
    const map = new Map<string, TagRegistryEntry>();
    for (const t of this.tags()) map.set(t.id, t);
    return map;
  });

  set(entries: TagRegistryEntry[]): void {
    this.workspaceTags.set(entries ?? []);
    this.workspaceLoaded = true;
    this.publish();
    this.activeProjectLoaded.set(!this.activeProject() || this.projectTags().has(this.activeProject()!));
  }

  loadProject(projectName: string | null): void {
    this.activeProject.set(projectName);
    this.activeProjectLoaded.set(this.workspaceLoaded && (!projectName || this.projectTags().has(projectName)));
    this.publish();
    if (projectName) this.ensureProject(projectName);
  }

  /** Load a project's registry without changing the active filter options. */
  ensureProject(projectName: string): void {
    if (this.projectTags().has(projectName) || this.pendingProjects.has(projectName)) return;
    this.pendingProjects.add(projectName);
    this.http.get<{ items: TagRegistryEntry[] }>(`/api/projects/${encodeURIComponent(projectName)}/tags`)
      .subscribe({
        next: response => {
          this.pendingProjects.delete(projectName);
          this.projectTags.update(current => new Map(current).set(projectName, response.items ?? []));
          if (this.activeProject() === projectName) {
            this.publish();
            this.activeProjectLoaded.set(this.workspaceLoaded);
          }
        },
        error: () => {
          this.pendingProjects.delete(projectName);
          if (this.activeProject() === projectName) this.activeProjectLoaded.set(this.workspaceLoaded);
        },
      });
  }

  /** Resolve chips against their owning project, including workspace tags. */
  byIdForProject(projectName: string | null): Map<string, TagRegistryEntry> {
    const byId = new Map(this.workspaceTags().map(tag => [tag.id, tag]));
    for (const tag of this.projectTags().get(projectName ?? '') ?? []) byId.set(tag.id, tag);
    return byId;
  }

  private publish(): void {
    const project = this.activeProject();
    const byId = new Map(this.workspaceTags().map(tag => [tag.id, tag]));
    if (project) {
      for (const tag of this.projectTags().get(project) ?? []) byId.set(tag.id, tag);
    }
    this.tags.set([...byId.values()]);
  }
}
