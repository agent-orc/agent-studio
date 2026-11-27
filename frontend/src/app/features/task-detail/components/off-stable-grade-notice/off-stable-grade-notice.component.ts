import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { TaskState, type TaskInfo } from '../../../../models/task.model';
import { TaskService } from '../../../../services/task.service';
import { NotificationService } from '@studio/ui/services/notification.service';
import { StatusIndicatorComponent } from '@studio/ui/components/status-indicator/status-indicator.component';

/**
 * AGT-3845: a Human Review card whose latest Remote Review grade came from a
 * review host that did not run the Stable build (AGT-3689) says so before the
 * operator accepts it, and offers a fresh review of every aspect on the build
 * the review hosts run now. The verdict itself stands; this names who made it.
 */
@Component({
  selector: 'app-off-stable-grade-notice',
  standalone: true,
  imports: [StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './off-stable-grade-notice.component.html',
  styleUrl: './off-stable-grade-notice.component.scss',
})
export class OffStableGradeNoticeComponent {
  readonly job = input.required<TaskInfo>();

  /** The `workerReleaseStable` sentence of the newest remote grade, in Human Review only. */
  readonly notice = computed(() => {
    const job = this.job();
    if (job.state !== TaskState.HumanReview) return null;
    const latest = job.reviewProjection?.attempts?.find(attempt => attempt.plane?.toLowerCase() === 'remote');
    return latest?.workerReleaseStable?.trim() || null;
  });
  readonly busy = signal(false);
  readonly requested = signal(false);
  readonly error = signal<string | null>(null);

  private readonly tasks = inject(TaskService);
  private readonly notifications = inject(NotificationService);

  reviewAgain(): void {
    if (this.busy()) return;
    const job = this.job();
    this.busy.set(true);
    this.error.set(null);
    this.tasks.reviewAgainOnCurrentBuild(job.id, job.watchPath ?? undefined).subscribe({
      next: () => {
        this.busy.set(false);
        this.requested.set(true);
        this.notifications.success('A fresh review on the current build was requested.');
        this.tasks.refresh(true);
      },
      error: (err: { error?: { error?: unknown } }) => {
        this.busy.set(false);
        const message = err?.error?.error;
        this.error.set(typeof message === 'string' && message.trim()
          ? message : 'The fresh review could not be requested.');
      },
    });
  }
}
