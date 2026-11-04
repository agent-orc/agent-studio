import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import type { TaskInfo } from '../../../../models/task.model';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { buildDecisionDamBadge, buildHumanReviewBadge } from '../task-card/task-card-view-model';
import { buildParkedBadge, type ParkedBoardBadge } from '../../../../models/parked-blocker-presentation';
import { StatusIndicatorComponent, type StatusTone } from '../../../../components/status-indicator/status-indicator.component';

/**
 * AGT-3403: a decision park waits for a person to choose (info); a failure park
 * waits for a fix, an operator step (warning). Unknown kinds stay neutral.
 */
export const PARK_KIND_TONES: Readonly<Record<ParkedBoardBadge['tone'], StatusTone>> = {
  decision: 'info',
  parked: 'warning',
};

export function parkKindTone(kind: string): StatusTone {
  return PARK_KIND_TONES[kind as ParkedBoardBadge['tone']] ?? 'neutral';
}
import { NowTickService } from '../../../../services/now-tick.service';

@Component({
  selector: 'app-review-decision-badges',
  standalone: true,
  imports: [TooltipDirective, StatusIndicatorComponent],
  templateUrl: './review-decision-badges.component.html',
  styleUrl: './review-decision-badges.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ReviewDecisionBadgesComponent {
  readonly job = input.required<TaskInfo>();
  readonly parkTone = parkKindTone;
  readonly decisionDamBadge = computed(() => buildDecisionDamBadge(this.job()));

  /**
   * AGT-2816: a parked card says on the board whether it waits for a PERSON
   * (an operator decision) or for a fix (a failure escalation). The generic
   * lane badge cannot tell the two apart, so the park badge replaces it
   * wherever a park marker exists rather than stacking a second chip.
   */
  private readonly now = inject(NowTickService).now;
  readonly parkedBadge = computed(() => buildParkedBadge(this.job(), this.now()));
  readonly humanReviewBadge = computed(() =>
    this.parkedBadge() ? null : buildHumanReviewBadge(this.job()));
}
