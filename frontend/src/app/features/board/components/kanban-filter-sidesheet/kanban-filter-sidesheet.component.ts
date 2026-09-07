import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  effect,
  inject,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { ClientSummary, TagRegistryEntry } from '../../../../models/task.model';
import { ModalStackService } from '../../../../services/modal-stack.service';
import { BoardFiltersService } from '../../state/board-filters.service';
import { TypeFilterOption } from '../filters-dropdown/filters-dropdown.component';

import { TooltipDirective } from 'coding-agent-chat/shared';
import { SidesheetComponent } from '../../../../components/sidesheet/sidesheet.component';
/**
 * VS Code-style right-edge sidesheet that hosts the board's search box and
 * faceted filters in one place. The board reflows live as the user types
 * and toggles facets — there is no separate result list, the kanban itself
 * is the result list. Visibility toggles (compact cards for now; future:
 * empty lanes, rail markers, tool activity) live in their own section so
 * "what do I want to see right now?" controls stay together.
 *
 * The component is a controlled view: it never owns query / facet / view
 * state. All sources of truth stay in `App` so URL hashing and existing
 * filter pills keep working unchanged. The component reads inputs and
 * emits outputs.
 *
 * The one exception is the AGT-2709 "waiting for release" facet, which is read
 * and written straight on `BoardFiltersService` (the same direct-injection
 * shape `ActiveBoardFiltersComponent` uses). The service is the actual source
 * of truth per ADR-0034 - `App` only re-exposes it - and routing a boolean
 * through the shell would grow an already over-budget `app.ts`.
 */
@Component({
  selector: 'app-kanban-filter-sidesheet',
  standalone: true,
  imports: [TooltipDirective, SidesheetComponent, NgTemplateOutlet],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './kanban-filter-sidesheet.component.html',
  styleUrl: './kanban-filter-sidesheet.component.scss',
  host: {
    '[class.is-open]': 'open()'
  }
})
export class KanbanFilterSidesheetComponent {
  readonly open = input<boolean>(false);
  /** When true, render the filter sections directly without the
   *  `<app-sidesheet>` chrome (no slide-in animation, no header/close
   *  button). Used to embed the same filter UI into the studio-shell's
   *  left sidebar filter panel so the user can scope the board from
   *  the activity-bar's filter icon instead of opening the right-edge
   *  sheet. */
  readonly inline = input<boolean>(false);
  readonly query = input<string>('');
  readonly typeOptions = input<readonly TypeFilterOption[]>([]);
  readonly activeType = input<string | null>(null);
  readonly tags = input<readonly TagRegistryEntry[]>([]);
  readonly activeTagIds = input<ReadonlySet<string>>(new Set<string>());
  readonly owners = input<readonly ClientSummary[]>([]);
  readonly activeOwnerId = input<string | null>(null);
  readonly hitCount = input<number>(0);
  readonly totalCount = input<number>(0);
  readonly hasAnyFilter = input<boolean>(false);

  readonly queryChange = output<string>();
  readonly setType = output<string | null>();
  readonly toggleTag = output<string>();
  readonly setOwner = output<string | null>();
  readonly closed = output<void>();
  readonly clearAll = output<void>();

  private readonly searchInputEl = viewChild<ElementRef<HTMLInputElement>>('searchInput');

  /** Suppress the input loop: when our own emit echoes back via the input
   *  binding we don't want to retrigger the change. Tracked via a signal so
   *  the effect that focuses on open is still pure. */
  private readonly localQuery = signal<string>('');

  private readonly boardFilters = inject(BoardFiltersService);
  /** AGT-2709 dependency facet; see the class comment for why it bypasses App. */
  readonly waitingForRelease = this.boardFilters.waitingForReleaseOnly;

  private readonly modalStack = inject(ModalStackService);
  private readonly destroyRef = inject(DestroyRef);
  private modalStackDispose: (() => void) | null = null;

  constructor() {
    effect(() => {
      if (this.open()) {
        // Defer one tick so the host's `is-open` width transition has
        // started; focusing inside a zero-width host steals focus
        // without scrolling it into view.
        queueMicrotask(() => this.searchInputEl()?.nativeElement.focus());
        if (!this.modalStackDispose) {
          this.modalStackDispose = this.modalStack.push('kanban-filter-sidesheet', () => this.close());
        }
      } else if (this.modalStackDispose) {
        this.modalStackDispose();
        this.modalStackDispose = null;
      }
    });
    this.destroyRef.onDestroy(() => {
      if (this.modalStackDispose) {
        this.modalStackDispose();
        this.modalStackDispose = null;
      }
    });
  }

  onInput(event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    this.localQuery.set(value);
    this.queryChange.emit(value);
  }

  onClear(): void {
    this.queryChange.emit('');
    queueMicrotask(() => this.searchInputEl()?.nativeElement.focus());
  }

  onEscape(event: Event): void {
    event.preventDefault();
    this.close();
  }

  onPickType(value: string): void {
    if (this.activeType() === value) {
      this.setType.emit(null);
    } else {
      this.setType.emit(value);
    }
  }

  onToggleTag(id: string): void {
    this.toggleTag.emit(id);
  }

  onSetOwner(id: string | null): void {
    this.setOwner.emit(id);
  }

  onToggleWaitingForRelease(): void {
    this.boardFilters.toggleWaitingForReleaseOnly();
  }

  onClearAll(): void {
    this.clearAll.emit();
  }

  close(): void {
    this.closed.emit();
  }
}
