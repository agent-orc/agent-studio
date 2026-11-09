import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import type { ContinueMode, ContinueRoundBase, ContinueTaskResponse, TaskInfo } from '../../../../models/task.model';
import { TaskState } from '../../../../models/task.model';
import { TaskService } from '../../../../services/task.service';
import { NotificationService } from '../../../../services/notification.service';
import { PendingButtonDirective } from '../../../../components/async-feedback';
import {
  SegmentedControlComponent,
  type SegmentedOption,
} from '../../../../components/segmented-control/segmented-control.component';
import { serverErrorMessage } from '../../../../utils/server-error-message';

type RoundMode = Extract<ContinueMode, 'continue' | 'steer' | 'extend'>;
type BaseRead = { state: 'loading' } | { state: 'ready'; base: ContinueRoundBase | null } | { state: 'error' };

const ROUND_LANES: ReadonlySet<string> = new Set([TaskState.HumanReview, TaskState.Completed]);

const BASE_SOURCES: Record<string, string> = {
  'latest-result': 'latest result',
  'review-fix-round': 'reviewed delivery of a fix round',
  existing: 'base saved by an earlier round',
  explicit: 'requested result',
};

/**
 * AGT-3582: a delivered card in Human review or Accepted takes another round
 * here, through `POST /api/tasks/{id}/continue`: the route and guards of
 * `studio-op continue`, batch isolation included. The round starts from the
 * card's latest result ref, shown read only; the reason is required so the
 * run trigger and the timeline say why the round was sent.
 */
@Component({
  selector: 'app-continue-round-panel',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PendingButtonDirective, SegmentedControlComponent],
  templateUrl: './continue-round-panel.component.html',
  styleUrl: './continue-round-panel.component.scss',
})
export class ContinueRoundPanelComponent {
  readonly job = input.required<TaskInfo>();

  readonly modeOptions: readonly SegmentedOption<RoundMode>[] = [
    { value: 'continue', label: 'Continue', testid: 'continue-round-mode-continue', tooltip: 'Resume the work with this follow-up.' },
    { value: 'steer', label: 'Steer', testid: 'continue-round-mode-steer', tooltip: 'Correct the direction of the delivered work.' },
    { value: 'extend', label: 'Extend', testid: 'continue-round-mode-extend', tooltip: 'Add scope on top of the delivered work.' },
  ];

  readonly applies = computed(() => ROUND_LANES.has(this.job().state));
  readonly open = signal(false);
  readonly mode = signal<RoundMode>('continue');
  readonly prompt = signal('');
  readonly reason = signal('');
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly baseRead = signal<BaseRead>({ state: 'loading' });
  readonly base = computed(() => {
    const read = this.baseRead();
    return read.state === 'ready' ? read.base : null;
  });
  readonly canSubmit = computed(() =>
    !this.busy() && this.prompt().trim().length > 0 && this.reason().trim().length > 0);

  private readonly tasks = inject(TaskService);
  private readonly notifications = inject(NotificationService);

  baseSource(base: ContinueRoundBase): string {
    return BASE_SOURCES[base.source] ?? base.source;
  }

  shortSha(sha: string): string {
    return sha.slice(0, 12);
  }

  openForm(): void {
    this.error.set(null);
    this.open.set(true);
    this.loadBase();
  }

  cancel(): void {
    if (this.busy()) return;
    this.reset();
  }

  onPromptInput(event: Event): void {
    this.prompt.set((event.target as HTMLTextAreaElement).value);
  }

  onReasonInput(event: Event): void {
    this.reason.set((event.target as HTMLInputElement).value);
  }

  submit(): void {
    if (!this.canSubmit()) return;
    const job = this.job();
    this.busy.set(true);
    this.error.set(null);
    this.tasks.continueJob(job.id, this.prompt().trim(), job.watchPath ?? undefined,
      undefined, undefined, undefined, this.mode(), undefined, this.reason().trim()).subscribe({
      next: (response) => {
        this.busy.set(false);
        this.notifications.success(roundOutcome(response));
        this.reset();
        this.tasks.refresh(true);
      },
      error: (err: unknown) => {
        this.busy.set(false);
        const message = serverErrorMessage(err, 'The round was not sent.');
        this.error.set(message);
        this.notifications.error(message, 'Continue refused');
      },
    });
  }

  private reset(): void {
    this.open.set(false);
    this.prompt.set('');
    this.reason.set('');
    this.mode.set('continue');
    this.error.set(null);
  }

  private loadBase(): void {
    const job = this.job();
    this.baseRead.set({ state: 'loading' });
    this.tasks.getContinueRoundBase(job.id, job.watchPath ?? undefined).subscribe({
      next: (answer) => this.baseRead.set({ state: 'ready', base: answer?.base ?? null }),
      error: () => this.baseRead.set({ state: 'error' }),
    });
  }
}

function roundOutcome(response: ContinueTaskResponse): string {
  if (response.status === 'started') return 'Round started on this card.';
  if (response.status === 'saved') return 'Round saved. Move the card to Ready to run it.';
  return response.base
    ? `Round queued. It starts from ${response.base.sha.slice(0, 12)}.`
    : 'Round queued. The card moved to Ready.';
}
