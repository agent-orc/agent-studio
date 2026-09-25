import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
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
  private readonly registry = inject(TagRegistryStore);
  readonly chips = computed(() => this.ids().map(id => this.registry.byId().get(id) ?? {
    id, label: id, description: 'Retired or unavailable tag', color: 'var(--studio-border)',
  }));
}
