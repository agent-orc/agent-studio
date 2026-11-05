import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  OnDestroy,
  ViewEncapsulation,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { NgClass } from '@angular/common';
import { StudioIconComponent, StudioIconName } from '../studio-icon/studio-icon.component';
import { CountBadgeComponent } from '../count-badge/count-badge.component';
import { MenuComponent } from '../menu/menu.component';
import type { MenuItem, MenuItemClickEvent } from '../menu/menu.types';

/**
 * Shape of a single tab in the shared {@link PaneTabsComponent} strip.
 *
 * The component is intentionally data-driven (vs. content projection per
 * tab) so panes can hand it an array of tabs and the visual chrome —
 * active-state, hover, focus, badge slot, indicator slot — stays
 * identical across surfaces.
 */
export interface PaneTabDef {
  /** Stable identifier emitted via `tabChange`. */
  readonly id: string;
  /** User-facing label rendered next to the icon. */
  readonly label: string;
  /** Optional studio-icon (preferred over emoji when both supplied). */
  readonly icon?: StudioIconName;
  /** Optional emoji/glyph; rendered when {@link icon} is omitted. */
  readonly emoji?: string;
  /** Numeric badge shown after the label (e.g. evidence count). */
  readonly badge?: number | string | null;
  /** Disables the button but keeps it in the strip. */
  readonly disabled?: boolean;
  /** Stable test hook; falls back to no `data-testid`. */
  readonly testid?: string;
  /**
   * Right-side indicator:
   *  - `spinner` for in-flight async work next to the label
   *  - `live` for a pulsing dot signalling an active stream
   */
  readonly indicator?: 'spinner' | 'live';
  /**
   * Optional CSS class suffix appended to the button (`pane-tab--<suffix>`).
   * Used by callers that want per-tab modifiers like activity ordering;
   * the component itself never reads it.
   */
  readonly modifier?: string;
}

export type PaneTabsVariant = 'header' | 'pill';

/**
 * Shared tab control used by panes that surface multiple sub-views in a
 * single header. Two visual variants:
 *
 *   - `header` — the canonical pane-header tab strip used by the prompt
 *     pane (Description / Evidence / Code Review). Full-height cells
 *     with a bottom-border accent on the active tab; sits inside the
 *     `tabs` slot of `<app-pane-header>`.
 *   - `pill`   — a compact pill toggle group used inside a pane body
 *     (e.g. the protocol pane's Protocol / Activity inspector switch).
 *
 * The component owns ARIA wiring (`role="tablist"` / `role="tab"`,
 * `aria-selected`) so every caller gets accessible tabs by default.
 */
@Component({
  selector: 'app-pane-tabs',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  encapsulation: ViewEncapsulation.None,
  host: {
    '[class.pane-tabs-host--header]': 'variant() === "header"',
  },
  imports: [NgClass, StudioIconComponent, CountBadgeComponent, MenuComponent],
  templateUrl: './pane-tabs.component.html',
  styleUrl: './pane-tabs.component.scss',
})
export class PaneTabsComponent implements AfterViewInit, OnDestroy {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private resizeObserver: ResizeObserver | null = null;
  private measurementFrame: number | null = null;
  private viewReady = false;
  private readonly onFontsLoaded = () => this.scheduleMeasurement();

  readonly tabs = input.required<readonly PaneTabDef[]>();
  readonly activeTabId = input.required<string>();
  readonly variant = input<PaneTabsVariant>('header');
  /** Optional modifier suffix applied to the container (e.g. `activity-first`). */
  readonly listModifier = input<string | null>(null);
  /** `aria-label` for the tablist container. */
  readonly ariaLabel = input<string | null>(null);
  /** ID of the content panel controlled by this strip. */
  readonly panelId = input.required<string>();
  /** Minimum readable width reserved for each inline tab. */
  readonly minimumTabWidth = input<number>(72);

  readonly tabChange = output<string>();
  readonly overflowOpen = signal(false);
  readonly overflowAnchor = signal<HTMLElement | null>(null);
  readonly availableWidth = signal<number | null>(null);
  readonly measuredTabWidths = signal<Readonly<Record<string, number>>>({});
  readonly measuredOverflowWidth = signal(0);
  readonly focusedTabId = signal<string | null>(null);

  private readonly tabMeasurementEffect = effect(
    () => {
      this.tabs();
      this.activeTabId();
      this.minimumTabWidth();
      if (this.viewReady) this.scheduleMeasurement();
    },
    { manualCleanup: true },
  );

  readonly inlineTabs = computed<readonly PaneTabDef[]>(() => {
    const tabs = this.tabs();
    if (this.variant() !== 'header') return tabs;
    const width = this.availableWidth();
    const tabWidths = this.measuredTabWidths();
    if (width === null || width <= 0 || tabs.some(tab => !tabWidths[tab.id])) return tabs;

    const totalWidth = tabs.reduce((sum, tab) => sum + tabWidths[tab.id], 0);
    if (totalWidth <= width + 0.5) return tabs;

    const availableForTabs = Math.max(0, width - this.measuredOverflowWidth());
    const active = tabs.find(tab => tab.id === this.activeTabId());
    const first = active ?? tabs[0];
    if (!first) return tabs;

    const visibleIds = new Set([first.id]);
    let visibleWidth = tabWidths[first.id];
    for (const tab of tabs) {
      if (visibleIds.has(tab.id)) continue;
      if (visibleWidth + tabWidths[tab.id] > availableForTabs + 0.5) break;
      visibleIds.add(tab.id);
      visibleWidth += tabWidths[tab.id];
    }
    return tabs.filter(tab => visibleIds.has(tab.id));
  });

  readonly overflowTabs = computed<readonly PaneTabDef[]>(() => {
    const visibleIds = new Set(this.inlineTabs().map(tab => tab.id));
    return this.tabs().filter(tab => !visibleIds.has(tab.id));
  });

  readonly overflowMenuItems = computed<readonly MenuItem[]>(() =>
    this.overflowTabs().map(tab => ({
      kind: 'row',
      id: tab.id,
      label: tab.label,
      disabled: tab.disabled,
      active: tab.id === this.activeTabId(),
      trailingBadge: tab.badge ? String(tab.badge) : undefined,
    })),
  );

  readonly overflowAriaLabel = computed(() => {
    const hiddenCount = this.overflowTabs().length;
    return `More tabs: ${hiddenCount} hidden ${hiddenCount === 1 ? 'tab' : 'tabs'}`;
  });

  readonly containerClass = computed(() => {
    const base = `pane-tabs pane-tabs--${this.variant()}`;
    const mod = this.listModifier();
    return mod ? `${base} pane-tabs--${mod}` : base;
  });

  ngAfterViewInit(): void {
    this.viewReady = true;
    if (typeof ResizeObserver !== 'undefined') {
      this.resizeObserver = new ResizeObserver(() => this.scheduleMeasurement());
      this.resizeObserver.observe(this.host.nativeElement);
    }
    if (typeof document !== 'undefined' && document.fonts) {
      void document.fonts.ready.then(() => this.scheduleMeasurement());
      document.fonts.addEventListener('loadingdone', this.onFontsLoaded);
    }
    this.scheduleMeasurement();
  }

  ngOnDestroy(): void {
    this.viewReady = false;
    this.tabMeasurementEffect.destroy();
    this.resizeObserver?.disconnect();
    if (this.measurementFrame !== null) cancelAnimationFrame(this.measurementFrame);
    if (typeof document !== 'undefined' && document.fonts) {
      document.fonts.removeEventListener('loadingdone', this.onFontsLoaded);
    }
  }

  private scheduleMeasurement(): void {
    if (!this.viewReady || typeof requestAnimationFrame === 'undefined') return;
    if (this.measurementFrame !== null) cancelAnimationFrame(this.measurementFrame);
    this.measurementFrame = requestAnimationFrame(() => {
      this.measurementFrame = null;
      this.measureTabs();
    });
  }

  private measureTabs(): void {
    const host = this.host.nativeElement;
    const availableWidth = host.getBoundingClientRect().width;
    if (availableWidth <= 0) return;

    const widths: Record<string, number> = {};
    for (const element of host.querySelectorAll<HTMLElement>('[data-pane-tab-measure]')) {
      const id = element.dataset['paneTabId'];
      if (id) widths[id] = element.getBoundingClientRect().width;
    }
    if (this.tabs().some(tab => !widths[tab.id])) return;

    const overflowMeasure = host.querySelector<HTMLElement>('[data-pane-overflow-measure]');
    this.availableWidth.set(availableWidth);
    this.measuredTabWidths.set(widths);
    this.measuredOverflowWidth.set(overflowMeasure?.getBoundingClientRect().width ?? 0);
    if (this.overflowTabs().length === 0) this.closeOverflow();
  }

  trackTab(_index: number, tab: PaneTabDef): string {
    return tab.id;
  }

  tabClass(tab: PaneTabDef): Record<string, boolean> {
    const active = tab.id === this.activeTabId();
    const classes: Record<string, boolean> = {
      'pane-tab': true,
      'pane-tab--active': active,
    };
    if (tab.modifier) {
      classes[`pane-tab--${tab.modifier}`] = true;
    }
    return classes;
  }

  onClick(tab: PaneTabDef): void {
    if (tab.disabled) return;
    this.focusedTabId.set(tab.id);
    if (tab.id === this.activeTabId()) return;
    this.tabChange.emit(tab.id);
  }

  tabId(tab: PaneTabDef): string {
    return `${this.panelId()}-tab-${tab.id}`;
  }

  tabIndex(tab: PaneTabDef): number {
    return tab.id === (this.focusedTabId() ?? this.activeTabId()) ? 0 : -1;
  }

  onTabKeydown(event: KeyboardEvent, tab: PaneTabDef): void {
    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault();
      this.onClick(tab);
      return;
    }
    const direction = event.key === 'ArrowRight' ? 1 : event.key === 'ArrowLeft' ? -1 : 0;
    if (!direction && event.key !== 'Home' && event.key !== 'End') return;
    const enabled = this.inlineTabs().filter(candidate => !candidate.disabled);
    if (!enabled.length) return;
    event.preventDefault();
    event.stopPropagation();
    const index = enabled.findIndex(candidate => candidate.id === tab.id);
    const next = event.key === 'Home' ? enabled[0]
      : event.key === 'End' ? enabled[enabled.length - 1]
      : enabled[(index + direction + enabled.length) % enabled.length];
    this.focusedTabId.set(next.id);
    Array.from(this.host.nativeElement.querySelectorAll<HTMLElement>('[role="tab"]'))
      .find(element => element.id === this.tabId(next))?.focus();
  }

  toggleOverflow(event: MouseEvent): void {
    event.stopPropagation();
    this.overflowAnchor.set(event.currentTarget as HTMLElement);
    this.overflowOpen.update(open => !open);
  }

  closeOverflow(): void {
    this.overflowOpen.set(false);
  }

  onOverflowItemClick(event: MenuItemClickEvent): void {
    const tab = this.overflowTabs().find(candidate => candidate.id === event.id);
    this.closeOverflow();
    if (tab) this.onClick(tab);
  }
}
