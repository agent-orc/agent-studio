import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import type { TaskIntegrationStatus } from '../../../git';
import type { ReviewProjectionView } from '../../../../models/task.model';
import { TaskService } from '../../../../services/task.service';
import { NotificationService } from '../../../../services/notification.service';
import { integrationDeadEnd } from './integration-dead-end.model';

@Component({
  selector: 'app-integration-dead-end',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './integration-dead-end.component.html',
  styleUrl: './integration-dead-end.component.scss',
})
export class IntegrationDeadEndComponent {
  readonly status = input<TaskIntegrationStatus | null | undefined>(null);
  readonly review = input<ReviewProjectionView | null | undefined>(null);
  readonly jobId = input.required<string>();
  readonly watchPath = input<string | null>(null);
  readonly containmentUnknown = input(false);
  readonly escalated = input(false);
  readonly recheckRequested = output<void>();
  readonly model = computed(() => integrationDeadEnd(this.status(), this.containmentUnknown(), this.review()));
  readonly busy = signal(false);
  readonly savedReason = signal<string | null>(null);
  private readonly tasks = inject(TaskService);
  private readonly notifications = inject(NotificationService);

  continueTask(): void {
    if (this.busy()) return;
    this.busy.set(true);
    this.tasks.continueFromFailure(this.jobId(), this.watchPath() ?? undefined).subscribe({
      next: (response) => {
        this.busy.set(false);
        if (response.status === 'saved') {
          this.savedReason.set(response.savedReason || 'reason unavailable');
        } else {
          this.savedReason.set(null);
          this.notifications.success(response.status === 'started'
            ? 'Continuation started on this task.'
            : 'Continuation queued. Task moved to Ready.');
        }
        this.tasks.refresh(true);
      },
      error: () => {
        this.busy.set(false);
        this.notifications.error('Could not queue the continuation.');
      },
    });
  }

  moveSavedToReady(): void {
    if (this.busy()) return;
    this.busy.set(true);
    this.tasks.moveJob(this.jobId(), '2-ready', this.watchPath() ?? undefined).subscribe({
      next: () => {
        this.busy.set(false);
        this.savedReason.set(null);
        this.notifications.success('Task moved to Ready. The saved continuation can run on pickup.');
        this.tasks.refresh(true);
      },
      error: () => {
        this.busy.set(false);
        this.notifications.error('Could not move the task to Ready.');
      },
    });
  }

  recheck(): void { this.recheckRequested.emit(); }

  retry(): void {
    this.tasks.retryIntegration(this.jobId(), this.watchPath() ?? undefined).subscribe({
      next: () => this.tasks.refresh(true),
      error: () => this.notifications.error('Could not retry integration.'),
    });
  }

  rebase(): void {
    this.tasks.queueIntegrationRecovery(this.jobId(), this.watchPath() ?? undefined).subscribe({
      next: () => this.tasks.refresh(true),
      error: () => this.notifications.error('Could not queue rebase recovery.'),
    });
  }
}
