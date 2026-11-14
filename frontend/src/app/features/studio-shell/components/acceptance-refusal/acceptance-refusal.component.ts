import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { StatusIndicatorComponent } from '../../../../components/status-indicator/status-indicator.component';
import { ConnectedOverlayDirective } from '../../../../directives/connected-overlay.directive';
import type { TaskInfo } from '../../../../models/task.model';
import { NotificationService } from '../../../../services/notification.service';
import { TaskService } from '../../../../services/task.service';
import { serverErrorMessage } from '../../../../utils/server-error-message';
import {
  AcceptanceRefusalStore,
  type AcceptanceRefusal,
} from '../../../task-detail/runtime';

/**
 * AGT-3704: the reason a Human Review Accept is held or was refused, rendered
 * in the task toolbar directly before the Accept button, with the one action
 * that resolves it. The owner override asks for a written reason, which the
 * server records on the card (`operatorOverride` on the move to Accepted).
 */
@Component({
  selector: 'app-acceptance-refusal',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [StatusIndicatorComponent, ConnectedOverlayDirective, TooltipDirective],
  templateUrl: './acceptance-refusal.component.html',
  styleUrl: './acceptance-refusal.component.scss',
})
export class AcceptanceRefusalComponent {
  readonly refusal = input.required<AcceptanceRefusal>();
  readonly job = input.required<TaskInfo>();
  readonly disabled = input(false);

  readonly overrideOpen = signal(false);
  readonly overrideAnchor = signal<HTMLElement | null>(null);
  readonly reason = signal('');
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly canSubmit = computed(() => !this.busy() && this.reason().trim().length > 0);

  private readonly tasks = inject(TaskService);
  private readonly notifications = inject(NotificationService);
  private readonly refusals = inject(AcceptanceRefusalStore);

  onAction(event: MouseEvent): void {
    if (this.disabled() || this.busy()) return;
    if (this.refusal().action === 'retry-integration') {
      this.retryIntegration();
      return;
    }
    this.error.set(null);
    this.overrideAnchor.set(event.currentTarget as HTMLElement);
    this.overrideOpen.update(open => !open);
  }

  onReasonInput(event: Event): void {
    this.reason.set((event.target as HTMLTextAreaElement).value);
  }

  cancelOverride(): void {
    if (this.busy()) return;
    this.overrideOpen.set(false);
    this.reason.set('');
    this.error.set(null);
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
        this.refusals.clear(job.taskKey);
        this.notifications.success('Accepted by owner override. The written reason is recorded on the card.');
        this.tasks.refresh(true);
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.error.set(serverErrorMessage(err, 'The override was not accepted.'));
      },
    });
  }

  private retryIntegration(): void {
    const job = this.job();
    this.busy.set(true);
    this.tasks.retryIntegration(job.id, job.watchPath ?? undefined).subscribe({
      next: () => {
        this.busy.set(false);
        this.refusals.clear(job.taskKey);
        this.notifications.success('Integration retry queued.');
        this.tasks.refresh(true);
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.notifications.error(serverErrorMessage(err, 'Could not retry integration.'));
      },
    });
  }
}
