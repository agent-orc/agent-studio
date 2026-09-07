import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import type { TaskInfo } from '../../../../models/task.model';
import { TooltipDirective } from 'coding-agent-chat/shared';
import {
  buildDecisionDamBadge,
  buildFailureClassBadge,
  buildHumanReviewBadge,
} from '../task-card/task-card-view-model';

@Component({
  selector: 'app-review-decision-badges',
  standalone: true,
  imports: [TooltipDirective],
  templateUrl: './review-decision-badges.component.html',
  styleUrl: './review-decision-badges.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ReviewDecisionBadgesComponent {
  readonly job = input.required<TaskInfo>();
  readonly decisionDamBadge = computed(() => buildDecisionDamBadge(this.job()));
  readonly humanReviewBadge = computed(() => buildHumanReviewBadge(this.job()));
  /**
   * AGT-2749: why a Review / Escalated card parked. Non-null only for a
   * non-product failure class, so a product failure keeps the lane quiet.
   */
  readonly failureClassBadge = computed(() => buildFailureClassBadge(this.job()));
}
