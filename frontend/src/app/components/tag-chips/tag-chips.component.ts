import { ChangeDetectionStrategy, Component, computed, effect, inject, input } from '@angular/core';
import { TagRegistryStore } from '../../services/tag-registry.store';

@Component({
  selector: 'app-tag-chips',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './tag-chips.component.html',
  styleUrl: './tag-chips.component.scss',
})
export class TagChipsComponent {
  readonly ids = input<readonly string[]>([]);
  readonly projectName = input<string | null>(null);
  private readonly registry = inject(TagRegistryStore);
  constructor() {
    effect(() => {
      const project = this.projectName();
      if (project && this.ids().length) this.registry.ensureProject(project);
    });
  }
  readonly chips = computed(() => {
    const registry = this.registry.byIdForProject(this.projectName());
    return this.ids().map(id => registry.get(id) ?? {
      id, label: id, description: 'Retired or unavailable tag', color: 'var(--studio-border)',
    });
  });
}
