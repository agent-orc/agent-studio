import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { MenuComponent, type MenuItem, type MenuItemClickEvent } from '@studio/ui/components/menu/index';

/** One open editor tab as the overflow menu lists it. */
export interface StudioTabOverflowEntry { readonly key: string; readonly label: string; }

/**
 * AGT-3836: on a phone the tab strip shows only the active tab. This control
 * lists every open tab so the others stay reachable without scrolling the
 * strip past the screen edge. The shell stylesheet shows it below 601 px only.
 */
@Component({
  selector: 'app-studio-tab-overflow',
  standalone: true,
  imports: [MenuComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './studio-tab-overflow.html',
  styleUrl: './studio-tab-overflow.scss',
})
export class StudioTabOverflowComponent {
  readonly entries = input.required<readonly StudioTabOverflowEntry[]>();
  readonly activeKey = input<string | null>(null);
  readonly tabSelect = output<string>();
  readonly open = signal(false);
  readonly ariaLabel = computed(() => `Open tabs: ${this.entries().length}`);
  readonly items = computed<readonly MenuItem[]>(() => this.entries().map(entry => ({
    kind: 'row', id: entry.key, label: entry.label, active: entry.key === this.activeKey(),
  })));

  toggle(event: Event): void {
    event.stopPropagation();
    this.open.update(open => !open);
  }

  close(): void {
    this.open.set(false);
  }

  onItem(event: MenuItemClickEvent): void {
    this.close();
    this.tabSelect.emit(event.id);
  }
}
