import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import type { UsageCostDetailView } from '../../usage-detail.util';

/** Cost breakdown shared by the desktop popover and phone sheet. */
@Component({
  selector: 'app-usage-cost-detail',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './usage-cost-detail.html',
  styleUrl: '../usage-detail-panel/usage-detail-panel.scss',
})
export class UsageCostDetailComponent {
  readonly cost = input.required<UsageCostDetailView>();
  readonly sectionId = input.required<string>();
}
