import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';

import { StudioIconComponent } from '../../../../components/studio-icon/studio-icon.component';
import type { UsageCostProjection } from '../../models/usage-cockpit.model';
import type { UsageAlarm } from '../../usage-alarm.policy';
import { buildCostChipView } from '../../usage-chip.util';

/**
 * Today's workspace cost chip (HUC-S2): the USD token-ledger estimate from
 * the cockpit projection, in the workspace-local day. It never converts quota
 * into money and never shows an unknown amount as zero. Activating it asks
 * the host to open the cost detail (HUC-S3). A latched budget alarm
 * (HUC-S5) adds the warning tint and mark; `Budget` is spelled out in the
 * accessible name and the detail.
 */
@Component({
  selector: 'app-usage-cost-chip',
  standalone: true,
  imports: [TooltipDirective, StudioIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './usage-cost-chip.html',
  styleUrl: './usage-cost-chip.scss',
})
export class UsageCostChipComponent {
  /** Cost projection; `null` while the cockpit snapshot loads. */
  readonly cost = input<UsageCostProjection | null>(null);
  /** Reference time for staleness, in epoch milliseconds. */
  readonly now = input<number>(Date.now());
  readonly expanded = input(false);
  /** Id of the detail dialog this chip opens. */
  readonly controls = input<string | null>(null);
  /** Latched HUC-S5 budget alarms. */
  readonly alarms = input<readonly UsageAlarm[]>([]);

  readonly activate = output<void>();

  readonly view = computed(() => buildCostChipView(this.cost(), this.now(), this.alarms()));
}
