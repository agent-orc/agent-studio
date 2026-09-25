import { Injectable, computed, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { TagRegistryEntry } from '../models/task.model';

/**
 * Process-wide cache of the workspace tag registry (`GET /api/tags`). The
 * root component refreshes it on init; consumers (cards, filter bar, tag
 * editor) read it as a signal so the UI re-renders the moment a tag is
 * added or removed without an extra round-trip.
 */
@Injectable({ providedIn: 'root' })
export class TagRegistryStore {
  private readonly http = inject(HttpClient);
  private readonly loadedProjects = new Set<string>();
  readonly tags = signal<TagRegistryEntry[]>([]);
  readonly byId = computed(() => {
    const map = new Map<string, TagRegistryEntry>();
    for (const t of this.tags()) map.set(t.id, t);
    return map;
  });

  set(entries: TagRegistryEntry[]): void {
    this.tags.set(entries ?? []);
  }

  loadProject(projectName: string): void {
    if (this.loadedProjects.has(projectName)) return;
    this.loadedProjects.add(projectName);
    this.http.get<{ items: TagRegistryEntry[] }>(`/api/projects/${encodeURIComponent(projectName)}/tags`)
      .subscribe({
        next: response => this.tags.update(current => {
          const byId = new Map(current.map(tag => [tag.id, tag]));
          for (const tag of response.items) byId.set(tag.id, tag);
          return [...byId.values()];
        }),
        error: () => this.loadedProjects.delete(projectName),
      });
  }
}
