import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import type { PromoteConceptResponse, PromoteConceptTasksResponse, TaskInfo } from '../../../../../models/task.model';
import { TaskService } from '../../../../../services/task.service';
import { NotificationService } from '../../../../../services/notification.service';
import { PendingButtonDirective } from '../../../../../components/async-feedback';
import { serverErrorMessage } from '../../../../../utils/server-error-message';

type Promotion =
  | { state: 'idle' }
  | { state: 'loading' }
  | { state: 'ready'; plan: PromoteConceptResponse }
  | { state: 'creating'; plan: PromoteConceptResponse }
  | { state: 'error'; message: string };

/**
 * AGT-3700: the continue guard refused a follow-up on a concept or planning
 * card. The refusal offers its two ways forward as buttons: send the same
 * follow-up again with the card's mode confirmed, or promote the card to
 * implementation. A concept card promotes here, from the validated
 * implementation proposals of its Dossier; a planning card hands over to the
 * create-task form that the title block already opens for it.
 */
@Component({
  selector: 'app-continue-mode-refusal',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PendingButtonDirective],
  templateUrl: './continue-mode-refusal.component.html',
  styleUrl: './continue-mode-refusal.component.scss',
})
export class ContinueModeRefusalComponent {
  readonly job = input.required<TaskInfo>();
  readonly message = input.required<string>();
  readonly busy = input(false);

  /** Resend the same follow-up with `modeOverride` set to the card's mode. */
  readonly keepAndSend = output<void>();
  /** A planning card: open the promote-to-coding form. */
  readonly promoteToCoding = output<void>();
  /** A concept card created its implementation cards; the round is over. */
  readonly promoted = output<void>();

  readonly mode = computed(() => this.job().mode === 'planning' ? 'planning' : 'concept');
  readonly promotion = signal<Promotion>({ state: 'idle' });
  readonly creating = computed(() => this.promotion().state === 'creating');
  readonly working = computed(() => this.busy() || this.promotion().state === 'loading' || this.creating());
  readonly plan = computed(() => {
    const read = this.promotion();
    return read.state === 'ready' || read.state === 'creating' ? read.plan : null;
  });
  readonly promoteError = computed(() => {
    const read = this.promotion();
    return read.state === 'error' ? read.message : null;
  });

  private readonly tasks = inject(TaskService);
  private readonly notifications = inject(NotificationService);

  promote(): void {
    if (this.working()) return;
    if (this.mode() === 'planning') {
      this.promoteToCoding.emit();
      return;
    }
    const job = this.job();
    this.promotion.set({ state: 'loading' });
    this.tasks.getPromoteConcept(job.id, job.watchPath ?? undefined).subscribe({
      next: (plan) => this.promotion.set({ state: 'ready', plan }),
      error: (err: unknown) => this.promotion.set({
        state: 'error',
        message: serverErrorMessage(err, 'The implementation proposals of this Dossier could not be read.'),
      }),
    });
  }

  createCards(): void {
    const read = this.promotion();
    if (read.state !== 'ready' || read.plan.items.length === 0) return;
    const job = this.job();
    this.promotion.set({ state: 'creating', plan: read.plan });
    this.tasks.promoteConcept(job.id, read.plan.items.map((_, index) => index), job.watchPath ?? undefined).subscribe({
      next: (result) => {
        this.promotion.set({ state: 'idle' });
        this.notifications.success(createdSummary(result));
        this.promoted.emit();
      },
      error: (err: unknown) => this.promotion.set({
        state: 'error',
        message: serverErrorMessage(err, 'The implementation cards were not created.'),
      }),
    });
  }

  back(): void {
    if (this.promotion().state === 'creating') return;
    this.promotion.set({ state: 'idle' });
  }
}

function createdSummary(result: PromoteConceptTasksResponse): string {
  const keys = result.created.map((task) => task.taskKey || task.jobId);
  const noun = keys.length === 1 ? 'implementation card' : 'implementation cards';
  return keys.length === 0 ? 'No implementation card was created.' : `Created ${keys.length} ${noun}: ${keys.join(', ')}.`;
}
