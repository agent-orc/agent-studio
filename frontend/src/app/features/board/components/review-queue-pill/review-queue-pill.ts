import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { TaskState, type TaskInfo } from '../../../../models/task.model';

@Component({
  selector: 'app-review-queue-pill',
  standalone: true,
  imports: [TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './review-queue-pill.html',
  styleUrl: './review-queue-pill.scss',
})
export class ReviewQueuePillComponent {
  readonly task = input.required<TaskInfo>();
  readonly autoReviewState = TaskState.AutoReview;
}
