import { ChangeDetectionStrategy, Component, ElementRef, computed, inject, input } from '@angular/core';

import type { UsageCockpitResponse } from '../../models/usage-cockpit.model';
import {
  buildUsageDetailView,
  CLI_MANAGEMENT_HREF,
  detailSectionId,
  type UsageDetailFocus,
} from '../../usage-detail.util';

/**
 * Usage detail content (HUC-S3): every CLI's quota windows and observed
 * models, then today's and this week's cost with project split, live runs and
 * slot pools. The desktop popover and the phone sheet render this same
 * component; only the surface around it differs.
 *
 * The panel always holds every section, so one chip action reaches all usage
 * detail. `focus` only decides which section heading receives focus.
 */
@Component({
  selector: 'app-usage-detail-panel',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './usage-detail-panel.html',
  styleUrl: './usage-detail-panel.scss',
})
export class UsageDetailPanelComponent {
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
    return detailSectionId(this.idPrefix(), focus);
  }

  cliFocus(cliId: string): UsageDetailFocus {
    return { kind: 'cli', cliId };
  }

  readonly costFocus: UsageDetailFocus = { kind: 'cost' };

  isSelected(focus: UsageDetailFocus): boolean {
    return this.sectionId(focus) === this.sectionId(this.focus());
  }

  /** Moves focus to the heading of the selected section and brings it into view. */
  focusSelected(): void {
    this.jumpTo(this.focus());
  }

  jumpTo(focus: UsageDetailFocus): void {
    const section = this.host.querySelector<HTMLElement>(`#${CSS.escape(this.sectionId(focus))}`);
    const heading = section?.querySelector<HTMLElement>('h3');
    if (!section || !heading) return;
    heading.focus({ preventScroll: true });
    section.scrollIntoView({ block: 'start' });
  }
}
