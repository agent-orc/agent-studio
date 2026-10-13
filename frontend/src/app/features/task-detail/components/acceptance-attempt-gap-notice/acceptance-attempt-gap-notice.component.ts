import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import type { TaskInfo } from '../../../../models/task.model';
import { TaskService } from '../../../../services/task.service';
import { NotificationService } from '../../../../services/notification.service';

/**
 * AGT-3076: a Human Review card whose run attempt the attempt authority store
 * no longer holds (store rebuilt into a new authority epoch) can only be
 * accepted with a written operator override. The detail says so before the
 * operator tries to accept, and AGT-3096 offers that override here: the
 * server records it against the delivery evidence named below.
 */
@Component({
  selector: 'app-acceptance-attempt-gap-notice',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './acceptance-attempt-gap-notice.component.html',
  styleUrl: './acceptance-attempt-gap-notice.component.scss',
})
export class AcceptanceAttemptGapNoticeComponent {
  readonly job = input.required<TaskInfo>();

  readonly gap = computed(() => this.job().acceptanceAttemptGap ?? null);
  readonly overrideOpen = signal(false);
  readonly reason = signal('');
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly canSubmit = computed(() => !this.busy() && this.reason().trim().length > 0);

  private readonly tasks = inject(TaskService);
  private readonly notifications = inject(NotificationService);

  openOverride(): void {
    this.error.set(null);
    this.overrideOpen.set(true);
  }

  cancelOverride(): void {
    if (this.busy()) return;
    this.overrideOpen.set(false);
    this.reason.set('');
    this.error.set(null);
  }

  onReasonInput(event: Event): void {
    this.reason.set((event.target as HTMLTextAreaElement).value);
  }

  acceptWithOverride(): void {
    if (!this.canSubmit()) return;
    const job = this.job();
    this.busy.set(true);
    this.error.set(null);
    this.tasks.moveJob(job.id, '6-completed', job.watchPath ?? undefined, undefined,
      this.reason().trim(), true).subscribe({
      next: () => {
        this.busy.set(false);
        this.overrideOpen.set(false);
        this.reason.set('');
        this.notifications.success('Accepted by operator override against the recorded delivery evidence.');
        this.tasks.refresh(true);
      },
      error: (err: { error?: { error?: unknown } }) => {
        this.busy.set(false);
        const message = err?.error?.error;
        this.error.set(typeof message === 'string' && message.trim()
          ? message : 'The override was not accepted.');
      },
    });
  }
}
