import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';

import { StudioIconComponent } from '../../../../components/studio-icon/studio-icon.component';
import type { UsageCli } from '../../models/usage-cockpit.model';
import { buildCliChipView } from '../../usage-chip.util';

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
  /** Reference time for staleness, in epoch milliseconds. */
  readonly now = input<number>(Date.now());
  readonly expanded = input(false);
  /** Id of the detail dialog this chip opens. */
  readonly controls = input<string | null>(null);

  readonly activate = output<string>();

  readonly view = computed(() => buildCliChipView(this.cliId(), this.cli(), this.timeZone(), this.now()));
}
