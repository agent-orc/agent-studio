import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { TaskState, type TaskInfo } from '../../../../models/task.model';
import { StatusIndicatorComponent, type StatusTone } from '../../../../components/status-indicator/status-indicator.component';

/** AGT-3403: an urgent review needs attention; a normal queue position is a quiet fact. */
export const REVIEW_PRIORITY_TONES: Readonly<Record<string, StatusTone>> = {
  urgent: 'warning',
  normal: 'neutral',
};

export function reviewPriorityTone(priority: string | null | undefined): StatusTone {
  return (priority && REVIEW_PRIORITY_TONES[priority]) || 'neutral';
}

@Component({
  selector: 'app-review-queue-pill',
  standalone: true,
  imports: [TooltipDirective, StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './review-queue-pill.html',
  styleUrl: './review-queue-pill.scss',
})
export class ReviewQueuePillComponent {
  readonly task = input.required<TaskInfo>();
  readonly autoReviewState = TaskState.AutoReview;
  readonly priorityTone = reviewPriorityTone;
}
