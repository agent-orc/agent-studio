import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
} from '@angular/core';
import { ModalStackService } from '@studio/ui/services/modal-stack.service';
import { ActiveFilterPill, BoardFiltersService } from '../../state/board-filters.service';

@Component({
  selector: 'app-active-board-filters',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './active-board-filters.component.html',
  styleUrl: './active-board-filters.component.scss',
  host: {
    '[class.is-visible]': 'pills().length > 0',
    '[class.has-zero-results]': 'pills().length > 0 && resultCount() === 0',
  },
})
export class ActiveBoardFiltersComponent {
  private readonly filters = inject(BoardFiltersService);
  private readonly modalStack = inject(ModalStackService);
  private escapeDispose: (() => void) | null = null;

  readonly resultCount = input.required<number>();
  readonly pills = this.filters.activeFilterPills;
  readonly filterDescription = computed(() => this.pills().map(pill => pill.label).join(', '));

  constructor() {
    const destroyRef = inject(DestroyRef);
    effect(() => {
      if (this.pills().length > 0 && !this.escapeDispose) {
        this.escapeDispose = this.modalStack.push(
          'active-board-filters',
          () => this.clearFilters(),
        );
      } else if (this.pills().length === 0 && this.escapeDispose) {
        this.escapeDispose();
        this.escapeDispose = null;
      }
    });
    destroyRef.onDestroy(() => this.escapeDispose?.());
  }

  removeFilter(pill: ActiveFilterPill): void {
    this.filters.removeFilterPill(pill);
  }

  clearFilters(): void {
    this.filters.clearSearchAndFilters();
  }

  pillIdentity(pill: ActiveFilterPill): string {
    return pill.kind === 'search' ? pill.kind : `${pill.kind}:${pill.value}`;
  }
}
