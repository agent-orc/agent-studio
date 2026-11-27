import { ChangeDetectionStrategy, Component, ElementRef, inject, input, output, signal, viewChild } from '@angular/core';
import { MenuComponent } from '@studio/ui/components/menu/menu.component';
import type { MenuItemClickEvent } from '@studio/ui/components/menu/menu.types';

export interface CockpitNavItem { id: string; label: string; inlineFrom: 'phone' | 'tablet' | 'desktop' | null; }
export interface UsageDetailRequest { section: 'cli' | 'cost' | 'details'; cliId: string | null; }
/** Narrowest header width at which an `inlineFrom` control shows inline; below it the More menu carries it. */
const INLINE_FROM_WIDTH = { phone: 601, tablet: 720, desktop: 1100 } as const;
const CLOSED_MENU_ITEMS: { kind: 'row'; id: string; label: string }[] = [];

/** One navigation row; compact destinations live in the text-only More menu. */
@Component({
  selector: 'app-studio-navigation-header', standalone: true, imports: [MenuComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './usage-cockpit-header.html', styleUrl: './usage-cockpit-header.scss',
  host: { role: 'banner' },
})
export class StudioNavigationHeaderComponent {
  readonly navItems = input<readonly CockpitNavItem[]>([]);
  readonly navSelect = output<string>();
  readonly moreOpen = signal(false);
  private readonly moreTrigger = viewChild<ElementRef<HTMLButtonElement>>('moreTrigger');
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  menuItems() {
    if (!this.moreOpen()) return CLOSED_MENU_ITEMS;
    const width = this.host.nativeElement.clientWidth;
    return this.navItems().filter(item => item.inlineFrom === null || width < INLINE_FROM_WIDTH[item.inlineFrom])
      .map(item => ({ kind: 'row' as const, id: item.id, label: item.label }));
  }
  onMoreClick(event: Event): void { event.stopPropagation(); this.moreOpen.update(open => !open); }
  onMenuItem(event: MenuItemClickEvent): void { this.closeMore(); this.navSelect.emit(event.id); }
  closeMore(): void { if (!this.moreOpen()) return; this.moreOpen.set(false); this.moreTrigger()?.nativeElement.focus(); }
  moreAnchor(): HTMLElement | null { return this.moreTrigger()?.nativeElement ?? null; }
}
