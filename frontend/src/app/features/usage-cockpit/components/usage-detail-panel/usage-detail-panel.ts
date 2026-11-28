import { usageStateTone } from '../usage-cost-detail/usage-detail-status';
import { StatusIndicatorComponent } from '@studio/ui/components/status-indicator/status-indicator.component';
import { ChangeDetectionStrategy, Component, ElementRef, OnDestroy, computed, inject, input, signal } from '@angular/core';
import { RemoteHostsService } from '../../../remote-hosts/services/remote-hosts.service';
import { DisclosureMarkerComponent } from '@studio/ui/components/disclosure-marker/disclosure-marker.component';

import { UsageCapacityDetailComponent } from '../usage-capacity-detail/usage-capacity-detail';
import { UsageCostDetailComponent } from '../usage-cost-detail/usage-cost-detail';
import type { UsageCockpitResponse } from '../../models/usage-cockpit.model';
import {
  buildUsageDetailView,
  CLI_MANAGEMENT_HREF,
  detailSectionId,
  type UsageDetailFocus,
  type UsageCliDetailView,
} from '../../usage-detail.util';

/**
 * Usage detail content (HUC-S3): every CLI's quota windows and observed
 * models, the Ready cards that would fall back or wait, then today's and this
 * week's cost with project split, live runs and slot pools. The desktop popover and the phone sheet render this same
 * component; only the surface around it differs.
 *
 * The panel always holds every section, so one footer entry reaches all usage
 * detail. `focus` only decides which section heading receives focus.
 */
@Component({
  selector: 'app-usage-detail-panel',
  standalone: true,
  imports: [StatusIndicatorComponent, UsageCapacityDetailComponent, UsageCostDetailComponent, DisclosureMarkerComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './usage-detail-panel.html',
  styleUrl: './usage-detail-panel.scss',
})
export class UsageDetailPanelComponent implements OnDestroy {
  readonly stateTone = usageStateTone;
  private readonly remoteHosts = inject(RemoteHostsService);
  readonly chatUsage = this.remoteHosts.interactiveUsage;
  readonly chatUsageOpen = signal(false);
  private chatUsagePoll: ReturnType<typeof setInterval> | null = null;
  toggleChatUsage(): void {
    this.chatUsageOpen.update(open => !open);
    if (this.chatUsageOpen()) {
      this.remoteHosts.refreshInteractiveUsage();
      this.chatUsagePoll = setInterval(() => this.remoteHosts.refreshInteractiveUsage(), 5_000);
    } else if (this.chatUsagePoll) {
      clearInterval(this.chatUsagePoll);
      this.chatUsagePoll = null;
    }
  }
  ngOnDestroy(): void { if (this.chatUsagePoll) clearInterval(this.chatUsagePoll); }
  readonly snapshot = input.required<UsageCockpitResponse>();
  readonly focus = input<UsageDetailFocus>({ kind: 'cost' });
  /** Reference time for countdowns and staleness, epoch milliseconds. */
  readonly now = input.required<number>();
  /** Prefix for section ids so two panels never share an id. */
  readonly idPrefix = input('usage-detail');

  readonly cliManagementHref = CLI_MANAGEMENT_HREF;

  private readonly host = inject(ElementRef<HTMLElement>).nativeElement as HTMLElement;

  readonly view = computed(() => buildUsageDetailView(this.snapshot(), this.now()));

  sectionId(focus: UsageDetailFocus): string {
    return detailSectionId(this.idPrefix(), this.resolveFocus(focus));
  }

  routingUsage(provider: string) {
    return this.snapshot().routingUsage?.find(usage => usage.provider === provider);
  }

  cliFocus(cli: UsageCliDetailView): UsageDetailFocus {
    return { kind: 'cli', cliId: cli.cliId, subscriptionKey: cli.subscriptionKey };
  }

  private resolveFocus(focus: UsageDetailFocus): UsageDetailFocus {
    if (focus.kind !== 'cli' || focus.subscriptionKey !== undefined) return focus;
    const account = this.view().clis.find(cli => cli.cliId.toLowerCase() === focus.cliId.toLowerCase());
    return account ? this.cliFocus(account) : focus;
  }

  readonly capacityFocus: UsageDetailFocus = { kind: 'capacity' };
  readonly costFocus: UsageDetailFocus = { kind: 'cost' };
  private readonly selectedFocus = signal<UsageDetailFocus | null>(null);

  isSelected(focus: UsageDetailFocus): boolean {
    return this.sectionId(focus) === this.sectionId(this.selectedFocus() ?? this.focus());
  }

  /** Moves focus to the heading of the selected section and brings it into view. */
  focusSelected(): void {
    this.jumpTo(this.focus());
  }

  jumpTo(focus: UsageDetailFocus): void {
    const section = this.host.querySelector<HTMLElement>(`#${CSS.escape(this.sectionId(focus))}`);
    const heading = section?.querySelector<HTMLElement>('h3');
    if (!section || !heading) return;
    this.selectedFocus.set(this.resolveFocus(focus));
    // Account labels can wrap the sticky navigation onto several lines.
    const navigation = this.host.querySelector<HTMLElement>('nav');
    if (navigation) section.style.scrollMarginTop = `${navigation.offsetHeight}px`;
    heading.focus({ preventScroll: true });
    section.scrollIntoView({ block: 'start' });
  }
}
