import { ChangeDetectionStrategy, Component, ElementRef, OnDestroy, ViewChild, computed, inject, input, output, signal } from '@angular/core';
import { TagRegistryEntry } from '../../../../models/task.model';
import { OverlayPortalDirective } from '@studio/ui/directives/overlay-portal.directive';
import { OverlayPortalService } from '@studio/ui/services/overlay-portal.service';

export interface TypeFilterOption {
  /** Backend-side value, e.g. `bug`, `feature`, `chore`. */
  value: string;
  /** Visible chip label, e.g. `Bugs`. */
  label: string;
  icon: string;
  /** CSS modifier suffix (`bug` / `feature` / `chore`). */
  kind: string;
}

/**
 * Combined task-type + tag filter dropdown. Replaces the previous inline
 * pill rows in the header so the chrome stays calm; the trigger button
 * shows a count badge of active filter selections (excluding the global
 * Owner / Project filters, which stay inline as their own controls).
 *
 * Type filter is single-select (one type or none); tag filter is
 * multi-select with AND semantics (a job needs all selected tags).
 */
@Component({
  selector: 'app-filters-dropdown',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [OverlayPortalDirective],
  templateUrl: './filters-dropdown.component.html',
  styleUrls: ['./filters-dropdown.component.scss']
})
export class FiltersDropdownComponent implements OnDestroy {
  readonly typeOptions = input.required<readonly TypeFilterOption[]>();
  readonly activeType = input<string | null>(null);
  readonly tags = input<readonly TagRegistryEntry[]>([]);
  readonly activeTagIds = input<ReadonlySet<string>>(new Set<string>());

  readonly setType = output<string | null>();
  readonly toggleTag = output<string>();

  readonly open = signal(false);
  readonly panelStyle = signal<{ top: string; left: string }>({ top: '0px', left: '0px' });

  @ViewChild('triggerBtn') private triggerBtn?: ElementRef<HTMLButtonElement>;
  @ViewChild('panel') private panel?: ElementRef<HTMLDivElement>;

  private readonly overlayPortal = inject(OverlayPortalService);
  private repositionAttached = false;
  private readonly reposition = () => this.positionPanel();

  readonly badgeCount = computed(() => {
    const types = this.activeType() ? 1 : 0;
    return types + this.activeTagIds().size;
  });

  toggle(): void {
    this.open.update(v => !v);
    if (this.open()) {
      queueMicrotask(() => {
        this.attachReposition();
        this.positionPanel();
      });
    } else {
      this.detachReposition();
    }
  }

  close(): void {
    this.open.set(false);
    this.detachReposition();
  }

  ngOnDestroy(): void {
    this.detachReposition();
  }

  isTypeActive(value: string): boolean {
    return this.activeType() === value;
  }

  isTagActive(id: string): boolean {
    return this.activeTagIds().has(id);
  }

  pickType(value: string): void {
    if (this.activeType() === value) {
      this.setType.emit(null);
    } else {
      this.setType.emit(value);
    }
  }

  pickAll(): void {
    this.setType.emit(null);
  }

  emitToggleTag(id: string): void {
    this.toggleTag.emit(id);
  }

  private positionPanel(): void {
    const trigger = this.triggerBtn?.nativeElement;
    const panel = this.panel?.nativeElement;
    if (!trigger || !panel) return;
    const pos = this.overlayPortal.positionConnected(trigger, panel, {
      preferredPlacement: 'below',
      alignment: 'end',
      gap: 6,
      viewportPadding: 8,
      minWidth: 280,
    });
    this.panelStyle.set({ top: `${pos.top}px`, left: `${pos.left}px` });
  }

  private attachReposition(): void {
    if (this.repositionAttached) return;
    this.repositionAttached = true;
    window.addEventListener('scroll', this.reposition, true);
    window.addEventListener('resize', this.reposition);
  }

  private detachReposition(): void {
    if (!this.repositionAttached) return;
    this.repositionAttached = false;
    window.removeEventListener('scroll', this.reposition, true);
    window.removeEventListener('resize', this.reposition);
  }
}
