import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  afterEveryRender,
  computed,
  inject,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';

import { MenuComponent } from '../../../../components/menu/menu.component';
import type { MenuItem, MenuItemClickEvent } from '../../../../components/menu/menu.types';
import type { UsageCockpitResponse } from '../../models/usage-cockpit.model';
import { cliDisplayName } from '../../usage-chip.util';
import {
  headerTierForWidth,
  narrowerTier,
  orderClis,
  planUsageHeader,
  tierAtLeast,
  type HeaderTier,
  type UsageChipFit,
  type UsageHeaderMeasures,
  type UsageHeaderPlan,
} from '../../usage-header-layout';
import { UsageCliChipComponent } from '../usage-cli-chip/usage-cli-chip';
import { UsageCostChipComponent } from '../usage-cost-chip/usage-cost-chip';

/**
 * One header destination. Inline from `inlineFrom` upward (the host renders
 * it with `data-nav-inline-from`); below that tier, or always when
 * `inlineFrom` is null, it is a text-only row in the More menu.
 */
export interface CockpitNavItem {
  id: string;
  label: string;
  inlineFrom: Exclude<HeaderTier, 'phone'> | null;
}

/** What a usage control asks the host to open. */
export interface UsageDetailRequest {
  section: 'cli' | 'cost' | 'details';
  cliId: string | null;
}

const FITS: readonly UsageChipFit[] = ['full', 'compact', 'abbreviated', 'bare'];

/**
 * Studio header with the usage cockpit (HUC-S4,
 * docs/header-usage-cockpit/index.html "Responsive layout").
 *
 * Desktop and tablet: a 44 px navigation row above a 40 px usage row (48 +
 * 48 px on coarse pointers). Phone: one 48 px row with the Studio identity,
 * the primary CLI weekly percentage, today's cost and the More menu. The
 * tier follows this header's own width, so container constraints and zoom
 * use the same priority rules as the breakpoints, and a measured content fit
 * moves whole CLI chips to Details before any value could be cut.
 *
 * The host projects its navigation controls and marks each optional one with
 * `data-nav-inline-from="tablet|desktop"`; the same ids listed in `navItems`
 * appear in the More menu whenever they are hidden inline.
 */
@Component({
  selector: 'app-usage-cockpit-header',
  standalone: true,
  imports: [MenuComponent, UsageCliChipComponent, UsageCostChipComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './usage-cockpit-header.html',
  styleUrl: './usage-cockpit-header.scss',
  host: {
    role: 'banner',
    '[attr.data-tier]': 'tier()',
    '[attr.data-nav-tier]': 'navTier()',
    '[attr.data-layout]': 'plan().layout',
    '[attr.data-fit]': 'plan().fit',
  },
})
export class UsageCockpitHeaderComponent {
  /** Cockpit projection; `null` while it loads. */
  readonly snapshot = input<UsageCockpitResponse | null>(null);
  /** CLIs listed while the projection loads, in stable provider order. */
  readonly placeholderCliIds = input<readonly string[]>(['codex', 'claude']);
  /** The operator's current CLI selection, if any. */
  readonly selectedCliId = input<string | null>(null);
  /** Saved workspace default CLI. */
  readonly defaultCliId = input<string | null>(null);
  readonly navItems = input<readonly CockpitNavItem[]>([]);
  /** Reference time for chip staleness (fixtures only). */
  readonly now = input<number | null>(null);
  /** Section whose detail is open, so its trigger reports `aria-expanded`. */
  readonly openDetail = input<UsageDetailRequest | null>(null);
  /** Id of the usage detail region the triggers control. */
  readonly detailControls = input<string | null>(null);

  readonly navSelect = output<string>();
  readonly usageSelect = output<UsageDetailRequest>();

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly measureLayer = viewChild<ElementRef<HTMLElement>>('measure');
  private readonly navContent = viewChild<ElementRef<HTMLElement>>('navContent');
  private readonly moreTrigger = viewChild<ElementRef<HTMLButtonElement>>('moreTrigger');
  private readonly detailsButton = viewChild<ElementRef<HTMLButtonElement>>('detailsButton');

  private readonly width = signal(0);
  private readonly measures = signal<UsageHeaderMeasures | null>(null);
  /** Narrower nav tier forced by a measured overflow at the current width. */
  private readonly navFit = signal<HeaderTier | null>(null);
  private lastFocusedInside: HTMLElement | null = null;

  readonly moreOpen = signal(false);

  readonly tier = computed<HeaderTier>(() => headerTierForWidth(this.width()));
  readonly navTier = computed<HeaderTier>(() => this.navFit() ?? this.tier());

  readonly timeZone = computed(() => this.snapshot()?.timeZone ?? null);

  /** CLIs in primary order; ids only while loading. */
  readonly orderedClis = computed(() => {
    const snap = this.snapshot();
    const clis = snap
      ? snap.clis
      : this.placeholderCliIds().map(cliId => ({ cliId } as UsageCockpitResponse['clis'][number]));
    return orderClis(clis, this.selectedCliId(), this.defaultCliId())
      .map(cli => ({ cliId: cli.cliId, cli: snap ? cli : null }));
  });
  readonly primary = computed(() => this.orderedClis()[0] ?? null);
  readonly secondaries = computed(() => this.orderedClis().slice(1));

  readonly plan = computed<UsageHeaderPlan>(() => {
    const m = this.measures();
    const tier = this.tier();
    if (!m || this.width() === 0) {
      return tier === 'phone'
        ? { layout: 'single', fit: 'compact', visibleSecondaries: 0, showWordmark: true, showDetails: false }
        : { layout: 'rows', fit: 'full', visibleSecondaries: 0, showWordmark: false, showDetails: true };
    }
    return planUsageHeader(this.width(), tier, m);
  });

  readonly visibleSecondaries = computed(() => this.secondaries().slice(0, this.plan().visibleSecondaries));
  readonly hiddenCliNames = computed(() => {
    const p = this.plan();
    const hidden = this.secondaries().slice(p.visibleSecondaries).map(c => cliDisplayName(c.cliId));
    return hidden;
  });
  readonly detailsLabel = computed(() => {
    const hidden = this.hiddenCliNames();
    return hidden.length
      ? `Usage details, including ${hidden.join(', ')}`
      : 'Usage details';
  });

  readonly menuItems = computed<MenuItem[]>(() => {
    const tier = this.navTier();
    return this.navItems()
      .filter(item => item.inlineFrom == null || !tierAtLeast(tier, item.inlineFrom))
      .map(item => ({ kind: 'row' as const, id: item.id, label: item.label }));
  });

  readonly fits = FITS;

  constructor() {
    const destroyRef = inject(DestroyRef);
    // Test environments without layout have no ResizeObserver; the plan then
    // keeps its breakpoint defaults.
    const observer = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(() => this.measure());
    observer?.observe(this.host.nativeElement);
    destroyRef.onDestroy(() => observer?.disconnect());

    const observedMeasures = new Set<HTMLElement>();
    const onFocusIn = (event: FocusEvent) => {
      this.lastFocusedInside = event.target instanceof HTMLElement ? event.target : null;
    };
    this.host.nativeElement.addEventListener('focusin', onFocusIn);
    destroyRef.onDestroy(() => this.host.nativeElement.removeEventListener('focusin', onFocusIn));

    afterEveryRender(() => {
      const layer = this.measureLayer()?.nativeElement ?? null;
      // The layer itself is fixed at 0 x 0. Observe its max-content children
      // so a polled value or newly selected CLI triggers a fresh fit even when
      // the header's width stays the same.
      const current = new Set(layer?.querySelectorAll<HTMLElement>('[data-measure]') ?? []);
      for (const element of observedMeasures) {
        if (!current.has(element)) {
          observer?.unobserve(element);
          observedMeasures.delete(element);
        }
      }
      for (const element of current) {
        if (!observedMeasures.has(element)) {
          observer?.observe(element);
          observedMeasures.add(element);
        }
      }
      this.measure();
      this.checkNavOverflow();
      this.restoreLostFocus();
    });
  }

  onMoreClick(event: Event): void {
    event.stopPropagation();
    this.moreOpen.update(open => !open);
  }

  onMenuItem(event: MenuItemClickEvent): void {
    this.closeMore();
    this.navSelect.emit(event.id);
  }

  closeMore(): void {
    if (!this.moreOpen()) return;
    this.moreOpen.set(false);
    this.moreTrigger()?.nativeElement.focus();
  }

  /** The More trigger, so a host can anchor a menu opened from it. */
  moreAnchor(): HTMLElement | null {
    return this.moreTrigger()?.nativeElement ?? null;
  }

  isExpanded(section: UsageDetailRequest['section'], cliId: string | null = null): boolean {
    const open = this.openDetail();
    return !!open && open.section === section && (section !== 'cli' || open.cliId === cliId);
  }

  private measure(): void {
    const host = this.host.nativeElement;
    const width = host.clientWidth;
    if (width !== this.width()) {
      this.width.set(width);
      this.navFit.set(null);
    }
    const layer = this.measureLayer()?.nativeElement;
    if (!layer) return;
    const w = (key: string) => {
      const el = layer.querySelector<HTMLElement>(`[data-measure="${key}"]`);
      return el ? Math.ceil(el.getBoundingClientRect().width) : 0;
    };
    const byFit = (prefix: string) =>
      Object.fromEntries(FITS.map(fit => [fit, w(`${prefix}-${fit}`)])) as Record<UsageChipFit, number>;
    const style = getComputedStyle(host);
    const px = (name: string, fallback: number) => parseFloat(style.getPropertyValue(name)) || fallback;
    const next: UsageHeaderMeasures = {
      primary: byFit('primary'),
      cost: byFit('cost'),
      secondaries: this.secondaries().map(c => w(`secondary-${c.cliId}`)),
      details: w('details'),
      wordmark: w('wordmark'),
      more: w('more'),
      gap: px('--cockpit-gap', 12),
      gutter: px('--cockpit-gutter', 16),
    };
    if (JSON.stringify(next) !== JSON.stringify(this.measures())) this.measures.set(next);
  }

  /** Collapse step 2: if the navigation still overflows, move one more tier into More. */
  private checkNavOverflow(): void {
    const nav = this.navContent()?.nativeElement;
    if (!nav || this.plan().layout === 'single') return;
    if (nav.scrollWidth > nav.clientWidth + 1 && this.navTier() !== 'phone') {
      this.navFit.set(narrowerTier(this.navTier()));
    }
  }

  /** A hidden destination hands focus to the control that still reaches it. */
  private restoreLostFocus(): void {
    const last = this.lastFocusedInside;
    if (!last) return;
    const active = document.activeElement;
    if (active && active !== document.body && active !== last) return;
    const gone = !last.isConnected || last.getClientRects().length === 0;
    if (!gone) return;
    this.lastFocusedInside = null;
    const wasNavigation = !!last.closest('.cockpit-header__nav-content');
    const target = wasNavigation || !this.plan().showDetails
      ? this.moreTrigger()?.nativeElement
      : this.detailsButton()?.nativeElement;
    target?.focus();
  }
}
