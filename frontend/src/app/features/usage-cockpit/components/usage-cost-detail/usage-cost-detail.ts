import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import type { UsageCostDetailView } from '../../usage-detail.util';
import { ReviewStatisticsComponent } from '../../../tokens/components/review-statistics/review-statistics';

/** Cost breakdown shared by the desktop popover and phone sheet. */
@Component({
  selector: 'app-usage-cost-detail',
  standalone: true,
  imports: [ReviewStatisticsComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './usage-cost-detail.html',
  styleUrl: '../usage-detail-panel/usage-detail-panel.scss',
})
export class UsageCostDetailComponent {
  readonly cost = input.required<UsageCostDetailView>();
  readonly sectionId = input.required<string>();
  readonly reviewProjects = computed(() => this.cost().projects.filter(project => !project.unattributed).map(project => project.name));
}
