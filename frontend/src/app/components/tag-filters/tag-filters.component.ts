import { ChangeDetectionStrategy, Component, computed, effect, inject, input } from '@angular/core';
// Service-only import avoids a cycle through the board barrel's components.
// eslint-disable-next-line no-restricted-imports
import { BoardFiltersService } from '../../features/board/state/board-filters.service';
import { TagRegistryStore } from '../../services/tag-registry.store';

/** One area/facet selection shared by the board, Dossier list and wiki. */
@Component({
  selector: 'app-tag-filters',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './tag-filters.component.html',
  styleUrl: './tag-filters.component.scss',
})
export class TagFiltersComponent {
  readonly projectName = input<string | null>(null);
  private readonly registry = inject(TagRegistryStore);
  readonly filters = inject(BoardFiltersService);
  readonly areas = computed(() => this.registry.tags().filter(tag => tag.kind === 'area'));
  readonly facets = computed(() => this.registry.tags().filter(tag => tag.kind !== 'area'));
  readonly area = computed(() => this.areas().find(tag => this.filters.activeTagFilter().has(tag.id))?.id ?? '');
  readonly facet = computed(() => this.facets().find(tag => this.filters.activeTagFilter().has(tag.id))?.id ?? '');
  constructor() { effect(() => { const project = this.projectName(); if (project) this.registry.loadProject(project); }); }

  select(kind: 'area' | 'facet', event: Event): void {
    const id = (event.target as HTMLSelectElement).value;
    const choices = kind === 'area' ? this.areas() : this.facets();
    const selected = new Set(this.filters.activeTagFilter());
    for (const choice of choices) selected.delete(choice.id);
    if (id) selected.add(id);
    this.filters.setTagSelection(selected);
  }
}
