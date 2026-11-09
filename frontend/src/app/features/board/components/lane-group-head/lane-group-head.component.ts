import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import type { LaneCardGroup } from '../review-grouping.util';
import { BulkLaneActionsComponent } from '../bulk-lane-actions/bulk-lane-actions.component';

/**
 * Heading of one in-column lane subsection. A review split shows its label
 * and count; an escalation burst (AGT-3585) adds when it began and the
 * actions valid for all of its cards.
 */
@Component({
  selector: 'app-lane-group-head',
  standalone: true,
  imports: [BulkLaneActionsComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './lane-group-head.component.html',
  styleUrl: './lane-group-head.component.scss',
})
export class LaneGroupHeadComponent {
  readonly group = input.required<LaneCardGroup>();
  readonly mutationsBlocked = input(false);

  since(iso: string): string {
    const date = new Date(iso);
    if (Number.isNaN(date.getTime())) return iso;
    return date.toLocaleString('en-US', { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
  }
}
