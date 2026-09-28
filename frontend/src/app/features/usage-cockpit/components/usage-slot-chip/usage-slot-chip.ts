import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';

import { DisclosureMarkerComponent } from '../../../../components/disclosure-marker/disclosure-marker.component';
import type { UsageSlotPool } from '../../models/usage-cockpit.model';
import { buildSlotChipView } from '../../usage-chip.util';

/**
 * Slot chip for the expanded usage view only (HUC-S2): remote, review and
 * auto pools side by side, each with its own occupancy and capacity. It is
 * never placed in the header strip and its counters are never added up. It
 * expands pool detail in place, so it carries the shared disclosure marker.
 */
@Component({
  selector: 'app-usage-slot-chip',
  standalone: true,
  imports: [DisclosureMarkerComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './usage-slot-chip.html',
  styleUrl: './usage-slot-chip.scss',
})
export class UsageSlotChipComponent {
  /** Slot pools; `null` while the cockpit snapshot loads. */
  readonly slots = input<readonly UsageSlotPool[] | null>(null);
  readonly expanded = input(false);
  /** Id of the pool detail region this chip expands. */
  readonly controls = input<string | null>(null);

  readonly activate = output<void>();

  readonly view = computed(() => buildSlotChipView(this.slots()));
}
