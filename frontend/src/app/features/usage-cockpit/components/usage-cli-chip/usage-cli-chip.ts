import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';

import { StudioIconComponent } from '../../../../components/studio-icon/studio-icon.component';
import type { UsageCli } from '../../models/usage-cockpit.model';
import type { UsageAlarm } from '../../usage-alarm.policy';
import { buildCliChipView } from '../../usage-chip.util';
import { injectUsageClock } from '../../usage-clock';

/**
 * One CLI usage chip (HUC-S2): the CLI name with its weekly and
 * current-session windows as one indivisible button. Activating it asks the
 * host to open that CLI's usage detail (HUC-S3 owns the popover or sheet);
 * the chip only reflects the host's `expanded` and `controls` state.
 *
 * HUC-S5: `alarms` are this CLI's latched alarms (warning or critical tint
 * with a warning mark, and `Limited` in words on the full chip). On the
 * primary chip, `hiddenAlarms` adds one nonnumeric mark for providers whose
 * chip is not visible. `compact` is the phone composition: weekly only.
 */
@Component({
  selector: 'app-usage-cli-chip',
  standalone: true,
  imports: [TooltipDirective, StudioIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './usage-cli-chip.html',
  styleUrl: './usage-cli-chip.scss',
})
export class UsageCliChipComponent {
  /** CLI id, e.g. `codex`. Names the chip while `cli` is still loading. */
  readonly cliId = input.required<string>();
  /** Projection for this CLI; `null` while the cockpit snapshot loads. */
  readonly cli = input<UsageCli | null>(null);
  /** Workspace IANA zone for reset and update times (UTC is shown alongside). */
  readonly timeZone = input<string | null>(null);
  /** Reference time for staleness; otherwise the chip follows its own clock. */
  readonly now = input<number | null>(null);
  readonly expanded = input(false);
  /** Id of the detail dialog this chip opens. */
  readonly controls = input<string | null>(null);
  /** Latched HUC-S5 alarms of this CLI. */
  readonly alarms = input<readonly UsageAlarm[]>([]);
  /** Alarms of CLIs whose chip is hidden; set on the primary chip only. */
  readonly hiddenAlarms = input<readonly UsageAlarm[]>([]);
  /** Phone composition: the weekly window only; the session stays in the sheet. */
  readonly compact = input(false);

  readonly activate = output<string>();

  private readonly clock = injectUsageClock();

  readonly view = computed(() => buildCliChipView(
    this.cliId(), this.cli(), this.timeZone(), this.now() ?? this.clock(), this.alarms(), this.hiddenAlarms()));
}
