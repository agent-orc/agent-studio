import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import type { TaskInfo } from '../../../../models/task.model';
import { TaskState } from '../../../../models/task.model';
import { TaskService } from '../../../../services/task.service';
import { NotificationService } from '../../../../services/notification.service';
import { ReviewerSession } from '../../../../services/reviewer-session.service';
import { PendingButtonDirective } from '../../../../components/async-feedback';
import { serverErrorMessage } from '../../../../utils/server-error-message';

/** Mirrors `HumanReviewDecisionEndpoints.MaxNoteLength`. */
export const MAX_REVIEW_NOTE_LENGTH = 4000;

type Pending = 'accept' | 'send-back' | null;

/**
 * AGT-3670: the Reviewer's decision on a card in Human Review. Accept moves
 * the card to Accepted; send back moves it to Ready with a note that the next
 * run receives as its follow-up. Shown only where the session reviews
 * without card write, so the person sees the one decision the server allows
 * instead of lane and run controls it would refuse.
 */
@Component({
  selector: 'app-human-review-decision-panel',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PendingButtonDirective],
  templateUrl: './human-review-decision-panel.component.html',
  styleUrl: './human-review-decision-panel.component.scss',
})
export class HumanReviewDecisionPanelComponent {
  readonly job = input.required<TaskInfo>();

  private readonly tasks = inject(TaskService);
  private readonly notifications = inject(NotificationService);
  private readonly reviewer = inject(ReviewerSession);

  readonly maxNoteLength = MAX_REVIEW_NOTE_LENGTH;
  readonly applies = computed(() =>
    this.job().state === TaskState.HumanReview && this.reviewer.reviewsOnly(this.job().projectName));
  readonly sendingBack = signal(false);
  readonly note = signal('');
  readonly pending = signal<Pending>(null);
  readonly error = signal<string | null>(null);
  readonly canSendBack = computed(() => this.pending() === null && this.note().trim().length > 0);

  openSendBack(): void {
    this.error.set(null);
    this.sendingBack.set(true);
  }

  cancel(): void {
    if (this.pending()) return;
    this.sendingBack.set(false);
    this.note.set('');
    this.error.set(null);
  }

  onNoteInput(event: Event): void {
    this.note.set((event.target as HTMLTextAreaElement).value);
  }

  accept(): void {
    if (this.pending()) return;
    const job = this.job();
    this.pending.set('accept');
    this.error.set(null);
    this.tasks.acceptHumanReview(job.id, job.watchPath ?? undefined).subscribe({
      next: () => this.settled('Accepted. The card moved to Accepted.'),
      error: (err: unknown) => this.failed(err, 'The card was not accepted.', 'Accept refused'),
    });
  }

  sendBack(): void {
    if (!this.canSendBack()) return;
    const job = this.job();
    this.pending.set('send-back');
    this.error.set(null);
    this.tasks.sendBackHumanReview(job.id, this.note().trim(), job.watchPath ?? undefined).subscribe({
      next: () => this.settled('Sent back. The card moved to Ready with your note for the next run.'),
      error: (err: unknown) => this.failed(err, 'The card was not sent back.', 'Send back refused'),
    });
  }

  private settled(message: string): void {
    this.pending.set(null);
    this.sendingBack.set(false);
    this.note.set('');
    this.notifications.success(message);
    this.tasks.refresh(true);
  }

  private failed(err: unknown, fallback: string, title: string): void {
    this.pending.set(null);
    const message = serverErrorMessage(err, fallback);
    this.error.set(message);
    this.notifications.error(message, title);
  }
}
