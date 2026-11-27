import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { TaskState, type TaskDeliveryClaimAnswer, type TaskInfo } from '../../../../models/task.model';
import { TaskService } from '../../../../services/task.service';
import { NotificationService } from '@studio/ui/services/notification.service';
import { StatusIndicatorComponent } from '@studio/ui/components/status-indicator/status-indicator.component';
import { AcceptanceReadinessStore } from '../../state/acceptance-readiness.store';
import { acceptanceOverrideAllowed, readinessPresentation } from '../../models/acceptance-readiness.model';
import { acceptancePrimaryReason, secondaryAcceptanceGaps } from '../../models/acceptance-primary-reason';
import { briefGapOf, briefGapSentence, unrecordedBriefSubject } from '../../models/delivery-brief-gap.model';
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
  /** F12: the card's other gaps, such as the integration hold, listed under the readiness reason. */
  readonly otherGaps = input<readonly string[]>([]);

  private readonly inReview = computed(() => this.job().state === TaskState.HumanReview);
  /** Immutable attribution fences evidence from previous delivery generations. */
  private readonly delivery = computed(() => {
    const job = this.job();
    const commit = [job.commit, ...(job.commits ?? []).slice().reverse()]
      .find(commit => commit && !commit.supersededByAttempt && !commit.supersededBySha);
    return { run: commit?.runAttemptId, ref: commit?.branch ?? job.integration?.deliveryRef };
  });
  private readonly checkKey = computed(() => this.inReview()
    ? `${this.job().taskKey}|${this.job().lastActivity ?? ''}|${this.delivery().run ?? ''}|${this.delivery().ref ?? ''}` : null);
  private readonly read = signal<{ pending: boolean; before: unknown }>({ pending: true, before: null });
  private readonly check = effect(onCleanup => {
    if (this.checkKey() === null) return;
    let active = true;
    onCleanup(() => { active = false; });
    untracked(() => {
      const job = this.job();
      const before = this.store.itemFor(job);
      this.read.set({ pending: true, before });
      const settled = () => { if (active) this.read.set({ pending: false, before }); };
      // checkCard retains its old answer on failure. Object identity tells us
      // whether this check (or a concurrent lane check) supplied a fresh answer.
      void this.store.checkCard(job).then(settled, settled);
    });
  });

  /** Keep passes as evidence, independently of the filtered refusal item. */
  readonly readiness = computed(() => {
    const answer = this.inReview() ? this.store.itemFor(this.job()) : null;
    const delivery = this.delivery();
    if (answer?.runAttemptId && delivery.run && answer.runAttemptId !== delivery.run) return null;
    const resultRef = answer?.latestResultRef?.replace(/^(?:refs\/heads\/|origin\/)/, '');
    const deliveryRef = delivery.ref?.replace(/^(?:refs\/heads\/|origin\/)/, '');
    // Immutable result refs and task branches can name the same delivery.
    if (resultRef && deliveryRef && !(answer?.runAttemptId && answer.runAttemptId === delivery.run)
      && resultRef.split('/')[0] === deliveryRef.split('/')[0] && resultRef !== deliveryRef) return null;
    return answer;
  });
  private readonly claimReview = computed(() => {
    const claim = this.inReview() ? this.contentReview() : null;
    const run = this.delivery().run ?? this.readiness()?.runAttemptId;
    return claim?.runAttemptId && run && claim.runAttemptId !== run ? null : claim;
  });
  readonly reviewSource = computed(() => {
    const answer = this.readiness();
    const claim = this.claimReview();
    if (!answer) return claim;
    // Dates order reviews only within the same run. Attempt/report identity
    // prevents a delayed copy of the same review from replacing readiness.
    const sameReview = !!claim && (answer.reviewAttemptId && claim.reviewAttemptId
      ? answer.reviewAttemptId === claim.reviewAttemptId
      : !!answer.reportRef && answer.reportRef === claim.reportRef);
    if (claim?.runAttemptId && claim.runAttemptId === answer.runAttemptId && !sameReview
      && Date.parse(claim.reviewedAtUtc ?? '') > Date.parse(answer.reviewedAtUtc ?? '')) return claim;
    return answer;
  });
  /** Claim brief facts are usable only with the selected review identity. */
  readonly briefReview = computed(() => {
    const claim = this.claimReview();
    const source = this.reviewSource();
    if (!claim || this.readiness()?.readiness === 'ready' && source === this.readiness()) return null;
    if (source !== claim && source && ((source.reviewAttemptId && claim.reviewAttemptId !== source.reviewAttemptId)
      || (source.reportRef && claim.reportRef !== source.reportRef))) return null;
    return claim;
  });
  readonly briefGap = computed(() => briefGapOf(this.briefReview()));
  readonly unrecordedBriefSubject = unrecordedBriefSubject;

  /** Claim-only evidence is provisional, never a verified pass or refusal. */
  readonly fallback = computed(() => {
    if (!this.inReview()) return null;
    const answer = this.readiness();
    const read = this.read();
    if (answer && this.reviewSource() === answer && answer !== read.before) return null;
    if (read.pending) return 'Checking acceptance readiness. The displayed review is provisional.';
    return answer && this.reviewSource() !== answer
      ? 'Acceptance readiness for this delivery has not been verified against the latest review.'
      : 'Acceptance readiness is unavailable. Acceptance for this delivery has not been verified.';
  });

  /** The refused dry run of this card; the run-attempt gap has its own notice. */
  readonly item = computed(() => {
    const item = this.readiness();
    return item && this.reviewSource() === item && item.readiness !== 'ready'
      && item.stage !== 'admitted' && item.stage !== 'lineage' ? item : null;
  });

  readonly verdictConflict = computed(() => !this.fallback() && this.job().orchestratorVerdict === 'accept'
    && ['concerns', 'block'].includes(this.reviewSource()?.contentStatus?.toLowerCase() ?? ''));
  readonly primary = computed(() => acceptancePrimaryReason(this.item()));
  readonly briefLeads = computed(() => !!this.briefGap()
    && (!this.primary() || this.primary()!.readiness === 'verdict-stale'));
  readonly secondaryGaps = computed(() => {
    const primary = this.primary();
    if (!primary) return [];
    return secondaryAcceptanceGaps(primary, [
      ...this.otherGaps(),
      this.briefLeads() ? null : briefGapSentence(this.briefGap(), this.briefReview()),
    ]);
  });

  readonly contentGap = computed(() => {
    const review = this.claimReview();
    const status = review?.contentStatus?.toLowerCase();
    return review && this.reviewSource() === review && !this.briefGap()
      && (status === 'concerns' || status === 'block') ? review : null;
  });

  readonly show = computed(() => !!this.fallback() || !!this.briefGap() || !!this.item() || !!this.contentGap());
  readonly tone = computed(() => readinessPresentation(this.item()?.readiness ?? 'blocked').tone);
  readonly canReviewAgain = computed(() => !this.fallback()
    && (!!this.briefGap() || !!this.contentGap() || this.item()?.stage === 'content'));
  readonly canAcceptWithReason = computed(() => !this.fallback() && acceptanceOverrideAllowed(this.item()));
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
        this.store.markAddressed(this.job());
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
