import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';

import { StudioIconComponent } from '../../../../components/studio-icon/studio-icon.component';
import type { UsageCli } from '../../models/usage-cockpit.model';
import type { UsageAlarm } from '../../usage-alarm.policy';
import { buildCliChipView } from '../../usage-chip.util';
import { cliAbbreviation, type UsageChipFit } from '../../usage-header-layout';
import { injectUsageClock } from '../../usage-clock';

/**
 * One CLI usage chip (HUC-S2): the CLI name with its weekly and
 * current-session windows as one indivisible button. Activating it asks the
 * host to open that CLI's usage detail (HUC-S3 owns the popover or sheet);
 * the chip only reflects the host's `expanded` and `controls` state.
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
  /**
   * Reference time for staleness, in epoch milliseconds. Without it the chip
   * follows its own clock, so a snapshot that ages past its TTL turns stale.
   */
  readonly now = input<number | null>(null);
  readonly expanded = input(false);
  /** Id of the detail dialog this chip opens. */
  readonly controls = input<string | null>(null);
  /** Latched quota and provider alarms. */
  readonly alarms = input<readonly UsageAlarm[]>([]);
  /** Nonnumeric alarms for CLI chips hidden by the responsive fit. */
  readonly hiddenAlarms = input<readonly UsageAlarm[]>([]);
  readonly compact = input(false);
  readonly readFailed = input(false);

  /**
   * Header fit (HUC-S4). `compact` and narrower show the weekly window only;
   * `abbreviated` shortens the provider; `bare` drops the visible tag. The
   * accessible name always carries every window.
   */
  readonly fit = input<UsageChipFit>('full');

  readonly activate = output<string>();

  private readonly clock = injectUsageClock();

  readonly view = computed(() => buildCliChipView(
    this.cliId(), this.cli(), this.timeZone(), this.now() ?? this.clock(), this.alarms(), this.hiddenAlarms(), this.readFailed()));

  readonly visibleName = computed(() => {
    const fit = this.fit();
    return fit === 'abbreviated' || fit === 'bare' ? cliAbbreviation(this.view().cliId) : this.view().name;
  });

  readonly windows = computed(() => {
    const v = this.view();
    return this.compact() || this.fit() !== 'full' ? [v.weekly] : [v.weekly, v.session];
  });
}
