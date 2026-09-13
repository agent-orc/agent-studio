import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { TaskInfo, type DecisionContent } from '../../../../models/task.model';
import { NotificationService } from '../../../../services/notification.service';
import { TaskService } from '../../../../services/task.service';

/**
 * AGT-2795: the call-to-action panel of a decision card. It opens on the
 * options, marks the recommendation, and gives the decider one "choose" action
 * plus a rationale field. Once decided it renders the recorded choice, rationale,
 * and a link to the durable record, with a reopen affordance. The card kind
 * gates rendering, so it stays inert on ordinary tasks.
 */
@Component({
  selector: 'app-decision-panel',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule],
  templateUrl: './decision-panel.component.html',
  styleUrl: './decision-panel.component.scss',
})
export class DecisionPanelComponent {
  private readonly tasks = inject(TaskService);
  private readonly notifications = inject(NotificationService);

  readonly job = input.required<TaskInfo>();
  readonly changed = output<void>();

  private readonly override = signal<DecisionContent | null>(null);
  readonly decision = computed<DecisionContent | null>(
    () => this.override() ?? this.job().decision ?? null);

  readonly show = computed(() => this.job().kind === 'decision' && !!this.decision());
  readonly open = computed(() => {
    const status = this.decision()?.status ?? 'requested';
    return status === 'requested' || status === 'reopened';
  });
  readonly options = computed(() => this.decision()?.options ?? []);
  readonly recommendedId = computed(() => this.decision()?.recommendedOptionId ?? null);
  readonly chosenId = computed(() => this.decision()?.chosenOptionId ?? null);

  readonly selectedOptionId = signal<string | null>(null);
  readonly rationale = signal('');
  readonly busy = signal(false);
  readonly reopening = signal(false);
  readonly reopenNote = signal('');

  private readonly reset = effect(() => {
    void this.job().taskKey;
    void this.job().decision?.status;
    this.override.set(null);
    this.selectedOptionId.set(null);
    this.rationale.set('');
    this.reopening.set(false);
    this.reopenNote.set('');
  });

  select(optionId: string): void {
    this.selectedOptionId.set(optionId);
  }

  canDecide = computed(() => !!this.selectedOptionId() && this.rationale().trim().length > 0 && !this.busy());

  decide(): void {
    const optionId = this.selectedOptionId();
    const rationale = this.rationale().trim();
    if (!optionId || !rationale || this.busy()) return;
    const job = this.job();
    this.busy.set(true);
    this.tasks.decideCard(job.id, optionId, rationale, job.watchPath).subscribe({
      next: (response) => {
        this.override.set(response.decision);
        this.busy.set(false);
        this.changed.emit();
        const unblocked = [...(response.unblocked ?? []), ...(response.created ?? [])];
        this.notifications.success(
          unblocked.length > 0 ? `Decision recorded. Unblocked ${unblocked.join(', ')}.` : 'Decision recorded.',
        );
      },
      error: (error) => {
        this.busy.set(false);
        this.notifications.warning(error?.error?.error || 'Could not record the decision.', 'Decision failed');
      },
    });
  }

  beginReopen(): void {
    this.reopenNote.set('');
    this.reopening.set(true);
  }

  cancelReopen(): void {
    this.reopening.set(false);
  }

  confirmReopen(): void {
    if (this.busy()) return;
    const job = this.job();
    this.busy.set(true);
    this.tasks.reopenDecision(job.id, this.reopenNote().trim() || undefined, job.watchPath).subscribe({
      next: (response) => {
        this.override.set(response.decision);
        this.busy.set(false);
        this.reopening.set(false);
        this.changed.emit();
        this.notifications.success('Decision reopened.');
      },
      error: (error) => {
        this.busy.set(false);
        this.notifications.warning(error?.error?.error || 'Could not reopen the decision.', 'Reopen failed');
      },
    });
  }
}
