import { ChangeDetectionStrategy, Component, ElementRef, inject, input, output, signal, viewChild } from '@angular/core';
import { MenuComponent } from '../../../../components/menu/menu.component';
import type { MenuItemClickEvent } from '../../../../components/menu/menu.types';

export interface CockpitNavItem { id: string; label: string; inlineFrom: 'tablet' | 'desktop' | null; }
export interface UsageDetailRequest { section: 'cli' | 'cost' | 'details'; cliId: string | null; }

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
    const width = this.host.nativeElement.clientWidth;
    return this.navItems().filter(item => item.inlineFrom === null
      || (item.inlineFrom === 'desktop' ? width < 1100 : width < 720))
      .map(item => ({ kind: 'row' as const, id: item.id, label: item.label }));
  }
  onMoreClick(event: Event): void { event.stopPropagation(); this.moreOpen.update(open => !open); }
  onMenuItem(event: MenuItemClickEvent): void { this.closeMore(); this.navSelect.emit(event.id); }
  closeMore(): void { if (!this.moreOpen()) return; this.moreOpen.set(false); this.moreTrigger()?.nativeElement.focus(); }
  moreAnchor(): HTMLElement | null { return this.moreTrigger()?.nativeElement ?? null; }
}
