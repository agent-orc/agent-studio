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
  readonly compact = input(false);
  private readonly registry = inject(TagRegistryStore);
  readonly filters = inject(BoardFiltersService);
  readonly areas = computed(() => this.registry.tags().filter(tag => tag.kind === 'area'));
  readonly facets = computed(() => this.registry.tags().filter(tag => tag.kind !== 'area'));
  readonly facetOptions = computed(() => {
    const facets = this.facets();
    const counts = new Map<string, number>();
    for (const tag of facets) {
      const label = tag.label.toLocaleLowerCase();
      counts.set(label, (counts.get(label) ?? 0) + 1);
    }
    const positions = new Map<string, number>();
    return facets.map(tag => {
      const label = tag.label.toLocaleLowerCase();
      const count = counts.get(label) ?? 1;
      const position = (positions.get(label) ?? 0) + 1;
      positions.set(label, position);
      return { id: tag.id, display: count > 1 ? `${tag.label} (${position} of ${count}, ${tag.id})` : tag.label };
    });
  });
  readonly area = computed(() => this.areas().find(tag => this.filters.activeTagFilter().has(tag.id))?.id ?? '');
  readonly facet = computed(() => this.facets().find(tag => this.filters.activeTagFilter().has(tag.id))?.id ?? '');
  /** Every active selection must remain visible, even before its registry loads. */
  readonly additionalSelections = computed(() => [...this.filters.activeTagFilter()]
    .filter(id => id !== this.area() && id !== this.facet()));
  constructor() {
    effect(() => this.registry.loadProject(this.projectName()));
    effect(() => {
      // The workspace view can contain tags from any project. Only a complete,
      // matching project registry can establish that a selected id is invalid.
      const project = this.projectName();
      if (!project || this.registry.activeProject() !== project || !this.registry.activeProjectLoaded()) return;
      const valid = new Set(this.registry.tags().map(tag => tag.id));
      const selected = this.filters.activeTagFilter();
      if ([...selected].some(id => !valid.has(id))) {
        this.filters.setTagSelection(new Set([...selected].filter(id => valid.has(id))));
      }
    });
  }

  select(kind: 'area' | 'facet', event: Event): void {
    const id = (event.target as HTMLSelectElement).value;
    this.selectId(kind, id);
  }

  private selectId(kind: 'area' | 'facet', id: string): void {
    const choices = kind === 'area' ? this.areas() : this.facets();
    const selected = new Set(this.filters.activeTagFilter());
    for (const choice of choices) selected.delete(choice.id);
    if (id) selected.add(id);
    this.filters.setTagSelection(selected);
  }

  removeSelection(id: string): void {
    const selected = new Set(this.filters.activeTagFilter());
    selected.delete(id);
    this.filters.setTagSelection(selected);
  }

  selectFacetByLabel(event: Event): void {
    const input = event.target as HTMLInputElement;
    const choice = this.facetOptions().find(option => option.display.toLocaleLowerCase() === input.value.toLocaleLowerCase());
    if (!input.value) this.selectId('facet', '');
    else if (choice) this.selectId('facet', choice.id);
  }

  facetLabel(): string {
    return this.facetOptions().find(option => option.id === this.facet())?.display ?? '';
  }
}
