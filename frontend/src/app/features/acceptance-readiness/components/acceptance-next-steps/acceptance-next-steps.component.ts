import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { TaskState, type TaskDeliveryClaimAnswer, type TaskInfo } from '../../../../models/task.model';
import { TaskService } from '../../../../services/task.service';
import { NotificationService } from '../../../../services/notification.service';
import { StatusIndicatorComponent } from '../../../../components/status-indicator/status-indicator.component';
import { AcceptanceReadinessStore } from '../../state/acceptance-readiness.store';
import { acceptanceOverrideAllowed, readinessPresentation } from '../../models/acceptance-readiness.model';
import { AcceptanceRoundDialogComponent } from '../acceptance-round-dialog/acceptance-round-dialog.component';

type ContentReview = NonNullable<TaskDeliveryClaimAnswer['contentReview']>;

/**
 * AGT-3867 - the next steps of a Human Review card whose acceptance would be
 * refused, in the card detail. The board card offered the round that closes
 * the gap; the detail offered only a brief-mismatch block, so a concern on the
 * current brief left the owner with no step. This block reads the same
 * completion dry run as the board and offers, for every refused class: the
 * prefilled round, a content review again, and an acceptance with a written
 * reason when the completion policy lets an override admit the card.
 */
@Component({
  selector: 'app-acceptance-next-steps',
  standalone: true,
  imports: [StatusIndicatorComponent, AcceptanceRoundDialogComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './acceptance-next-steps.html',
  styleUrl: './acceptance-next-steps.scss',
})
export class AcceptanceNextStepsComponent {
  private readonly tasks = inject(TaskService);
  private readonly notifications = inject(NotificationService);
  readonly store = inject(AcceptanceReadinessStore);

  readonly job = input.required<TaskInfo>();
  readonly contentReview = input<ContentReview | null>(null);

  private readonly inReview = computed(() => this.job().state === TaskState.HumanReview);
  /** Re-check when the card changes, for example after a round or a review. */
  private readonly checkKey = computed(() =>
    this.inReview() ? `${this.job().id}|${this.job().lastActivity ?? ''}` : null);

  private readonly check = effect(() => {
    if (this.checkKey() === null) return;
    untracked(() => void this.store.checkCard(this.job()));
  });

  readonly staleBrief = computed(() => {
    const review = this.contentReview();
    return this.inReview() && !!review
      && (review.deliveryBriefVersion !== review.currentBriefVersion
        || review.reviewBriefVersion !== review.currentBriefVersion);
  });

  /** The refused dry run of this card; the run-attempt gap has its own notice. */
  readonly item = computed(() => {
    const item = this.inReview() ? this.store.itemFor(this.job()) : null;
    return item && item.readiness !== 'ready' && item.stage !== 'admitted' && item.stage !== 'lineage' ? item : null;
  });

  readonly show = computed(() => this.staleBrief() || !!this.item());
  readonly tone = computed(() => readinessPresentation(this.item()?.readiness ?? 'blocked').tone);
  readonly canReviewAgain = computed(() => this.staleBrief() || this.item()?.stage === 'content');
  readonly canAcceptWithReason = computed(() => acceptanceOverrideAllowed(this.item()?.refusal));
  readonly roundDraft = computed(() => {
    const draft = this.store.draftFor('detail');
    return draft && draft.job.id === this.job().id ? draft : null;
  });

  readonly overrideOpen = signal(false);
  readonly writtenReason = signal('');
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly difference = signal<{ fromVersion: string; toVersion: string; before: string; after: string } | null>(null);
  readonly differenceOpen = signal(false);
  readonly differenceError = signal<string | null>(null);

  briefLabel(version: string | null | undefined): string {
    return version ? version.slice(0, 12) : 'unknown';
  }

  openRound(): void {
    this.store.openRound(this.job(), 'detail');
  }

  showDifference(): void {
    this.differenceOpen.set(true);
    this.differenceError.set(null);
    this.tasks.getBriefDifference(this.job().id, this.job().watchPath).subscribe({
      next: difference => this.difference.set(difference),
      error: () => this.differenceError.set('The earlier brief text is unavailable for this edit.'),
    });
  }

  reviewAgain(): void {
    if (this.busy()) return;
    this.busy.set(true);
    this.error.set(null);
    this.tasks.reviewContentAgainstCurrentBrief(this.job().id, this.job().watchPath).subscribe({
      next: () => {
        this.busy.set(false);
        this.notifications.success('Content review requested for the current brief.');
        this.tasks.refresh(true);
      },
      error: (error: { error?: { error?: string } }) => {
        this.busy.set(false);
        this.error.set(error.error?.error || 'Could not request content review.');
      },
    });
  }

  acceptWithReason(): void {
    const reason = this.writtenReason().trim();
    if (this.busy() || reason.length < 8) return;
    this.busy.set(true);
    this.error.set(null);
    const job = this.job();
    this.tasks.moveJob(job.id, TaskState.Completed, job.watchPath, undefined, reason, true).subscribe({
      next: () => {
        this.busy.set(false);
        this.overrideOpen.set(false);
        this.writtenReason.set('');
        this.notifications.success('Accepted with a written reason.');
        this.tasks.refresh(true);
      },
      error: (error: { error?: { error?: string } }) => {
        this.busy.set(false);
        this.error.set(error.error?.error || 'Could not accept this delivery.');
      },
    });
  }
}
