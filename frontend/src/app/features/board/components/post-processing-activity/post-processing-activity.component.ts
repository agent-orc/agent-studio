import {
  ChangeDetectionStrategy,
  Component,
  OnDestroy,
  OnInit,
  computed,
  inject,
  input,
  signal,
} from '@angular/core';
import type { TaskInfo } from '../../../../models/task.model';
import { AutoReviewStatusStore } from '../../../../services/auto-review-status.store';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { buildAutoReviewProcessBadge, type AutoReviewProcessBadge } from '../task-card/task-card-view-model';
import { StatusIndicatorComponent, type StatusTone } from '../../../../components/status-indicator/status-indicator.component';

/** AGT-3403: a running gate step spins; a queued gate slot needs attention; an idle wait stays calm. */
export const POST_PROCESSING_ACTIVITY_TONES: Readonly<Record<AutoReviewProcessBadge['tone'], StatusTone>> = {
  active: 'running',
  'gate-queued': 'warning',
  waiting: 'neutral',
};

export function postProcessingActivityTone(state: string): StatusTone {
  return POST_PROCESSING_ACTIVITY_TONES[state as AutoReviewProcessBadge['tone']] ?? 'neutral';
}

const nowTick = signal(Date.now());
if (typeof window !== 'undefined') {
  setInterval(() => nowTick.set(Date.now()), 30_000);
}

@Component({
  selector: 'app-post-processing-activity',
  standalone: true,
  imports: [TooltipDirective, StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './post-processing-activity.component.html',
  styleUrl: './post-processing-activity.component.scss',
})
export class PostProcessingActivityComponent implements OnInit, OnDestroy {
  readonly job = input.required<TaskInfo>();
  private readonly statusStore = inject(AutoReviewStatusStore);

  readonly activityTone = postProcessingActivityTone;

  readonly activity = computed(() =>
    buildAutoReviewProcessBadge(this.job(), this.statusStore.status(), nowTick()));

  ngOnInit(): void {
    this.statusStore.subscribe();
  }

  ngOnDestroy(): void {
    this.statusStore.release();
  }
}
