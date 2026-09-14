import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import type { TaskInfo } from '../../../../models/task.model';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { buildDecisionDamBadge, buildHumanReviewBadge } from '../task-card/task-card-view-model';
import { buildParkedBadge } from '../../../../models/parked-blocker-presentation';

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

  /**
   * AGT-2816: a parked card says on the board whether it waits for a PERSON
   * (an operator decision) or for a fix (a failure escalation). The generic
   * lane badge cannot tell the two apart, so the park badge replaces it
   * wherever a park marker exists rather than stacking a second chip.
   */
  readonly parkedBadge = computed(() => buildParkedBadge(this.job()));
  readonly humanReviewBadge = computed(() =>
    this.parkedBadge() ? null : buildHumanReviewBadge(this.job()));
}
