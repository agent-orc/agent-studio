import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import type { TaskIntegrationStatus } from '../../../git';
import type { FailureContinuationCheck, ReviewProjectionView } from '../../../../models/task.model';
import { TaskService } from '../../../../services/task.service';
import { NotificationService } from '../../../../services/notification.service';
import { serverErrorMessage } from '../../../../utils/server-error-message';
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
  /**
   * AGT-3582: the failure continuation guard, asked before the action is
   * offered. Only a delivery the server would continue shows the button.
   */
  readonly continueCheck = signal<FailureContinuationCheck | null>(null);
  readonly continueAvailable = computed(() => this.continueCheck()?.available === true);
  private readonly tasks = inject(TaskService);
  private readonly notifications = inject(NotificationService);
  private checkedFor: string | null = null;

  constructor() {
    effect(() => {
      const deadEnd = this.model();
      const status = this.status();
      const key = deadEnd && !deadEnd.recheck
        ? [this.jobId(), deadEnd.kind, status?.status, status?.deliveryRef, status?.failure?.stage].join('|')
        : null;
      untracked(() => this.loadCheck(key));
    });
  }

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
        this.recheckContinuation();
        this.tasks.refresh(true);
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.notifications.error(serverErrorMessage(err, 'Could not queue the continuation.'));
        this.recheckContinuation();
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
      error: (err: unknown) => {
        this.busy.set(false);
        this.notifications.error(serverErrorMessage(err, 'Could not move the task to Ready.'));
      },
    });
  }

  recheck(): void { this.recheckRequested.emit(); }

  private recheckContinuation(): void {
    const key = this.checkedFor;
    this.checkedFor = null;
    this.loadCheck(key);
  }

  private loadCheck(key: string | null): void {
    if (key === this.checkedFor) return;
    this.checkedFor = key;
    this.continueCheck.set(null);
    if (key === null) return;
    this.tasks.checkFailureContinuation(this.jobId(), this.watchPath() ?? undefined).subscribe({
      next: (check) => { if (this.checkedFor === key) this.continueCheck.set(check); },
      // Without an answer the action stays hidden rather than offered blind.
      error: () => { if (this.checkedFor === key) this.continueCheck.set(null); },
    });
  }

  retry(): void {
    this.tasks.retryIntegration(this.jobId(), this.watchPath() ?? undefined).subscribe({
      next: () => this.tasks.refresh(true),
      error: (err: unknown) => this.notifications.error(serverErrorMessage(err, 'Could not retry integration.')),
    });
  }

  rebase(): void {
    this.tasks.queueIntegrationRecovery(this.jobId(), this.watchPath() ?? undefined).subscribe({
      next: () => this.tasks.refresh(true),
      error: (err: unknown) => this.notifications.error(serverErrorMessage(err, 'Could not queue rebase recovery.')),
    });
  }
}
