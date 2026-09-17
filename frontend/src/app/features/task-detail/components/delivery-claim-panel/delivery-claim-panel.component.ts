import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
} from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import {
  TaskState,
  type TaskCompletionClaim,
  type TaskDeliveryClaimAnswer,
  type TaskInfo,
} from '../../../../models/task.model';
import { TaskService } from '../../../../services/task.service';

/** Tri-state containment answer. `unknown` is a question, never a negative. */
export type DeliveryIntegrationState =
  | 'integrated'
  | 'not-integrated'
  | 'nothing-to-integrate'
  | 'unknown';

const DELIVERED_LANES: readonly string[] = [
  TaskState.AutoReview,
  TaskState.HumanReview,
  TaskState.Escalated,
  TaskState.Completed,
  TaskState.Archive,
];

/**
 * AGT-2817 - the card's delivery statement, where the operator already looks.
 *
 * Before this, a card's integration state surfaced only inside the archive
 * modal, phrased as "pending", and nowhere on the card itself; the supersede
 * marker surfaced only inside a collapsed block in the Git tab. Both are
 * claims the card makes, so both belong next to the delivery:
 *
 * - the delivery ref,
 * - integrated yes/no, with the merge that carried it,
 * - contained in the released line yes/no,
 * - whether the attributed delivery is marked replaced, or only requeued with
 *   its replacement still pending,
 * - the ground the card completed on, including an override's written reason.
 *
 * Containment decides every yes/no here. A missing record is reported as
 * "not checked yet", never as "not integrated": the per-card answer is fetched
 * once for a delivered card, so an absent board projection is resolved rather
 * than assumed.
 */
@Component({
  selector: 'app-delivery-claim-panel',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [TooltipDirective],
  templateUrl: './delivery-claim-panel.component.html',
  styleUrl: './delivery-claim-panel.component.scss',
})
export class DeliveryClaimPanelComponent {
  private readonly tasks = inject(TaskService);

  readonly job = input.required<TaskInfo>();

  readonly answer = signal<TaskDeliveryClaimAnswer | null>(null);

  readonly inDeliveredLane = computed(() => DELIVERED_LANES.includes(this.job().state));

  private readonly load = effect((onCleanup) => {
    const job = this.job();
    this.answer.set(null);
    if (!DELIVERED_LANES.includes(job.state)) return;
    const subscription = this.tasks
      .getDeliveryClaim(job.id, job.watchPath ?? undefined)
      .subscribe({
        next: (answer) => this.answer.set(answer),
        // A failed lookup leaves the panel on the board projection and on
        // "not checked yet"; it never invents a negative.
        error: () => this.answer.set(null),
      });
    onCleanup(() => subscription.unsubscribe());
  });

  readonly deliveryRef = computed(
    () => this.answer()?.deliveryRef ?? this.job().integration?.deliveryRef ?? null,
  );

  readonly integrationBranch = computed(
    () => this.answer()?.integrationBranch || this.job().integration?.integrationBranch || 'develop',
  );

  readonly releaseBranch = computed(
    () => this.answer()?.releaseBranch || this.job().integration?.releaseBranch || 'main',
  );

  readonly integration = computed<DeliveryIntegrationState>(() => {
    const answer = this.answer();
    if (answer) {
      if (answer.integrated) return 'integrated';
      if (answer.containmentStatus === 'no-branch') return 'nothing-to-integrate';
      return answer.containmentStatus === 'unknown' ? 'unknown' : 'not-integrated';
    }
    switch (this.job().integration?.status) {
      // AGT-2849: the delivery is in the integration branch graph either way.
      // The panel reports containment, and the badge carries the unpublished
      // half of the answer.
      case 'integrated':
      case 'merged-locally': return 'integrated';
      case 'no-branch': return 'nothing-to-integrate';
      case 'pending':
      case 'partial':
      case 'conflict-skipped': return 'not-integrated';
      default: return 'unknown';
    }
  });

  readonly integrationLabel = computed(() => {
    switch (this.integration()) {
      case 'integrated': return `In ${this.integrationBranch()}`;
      case 'not-integrated': return `Not in ${this.integrationBranch()}`;
      case 'nothing-to-integrate': return 'Nothing to integrate';
      default: return 'Not checked yet';
    }
  });

  readonly integrationTooltip = computed(() => {
    const detail = this.answer()?.detail ?? this.job().integration?.detail ?? null;
    switch (this.integration()) {
      case 'integrated':
        return detail ?? `The delivery is contained in ${this.integrationBranch()}.`;
      case 'not-integrated':
        return detail ?? `The delivery is not contained in ${this.integrationBranch()}.`;
      case 'nothing-to-integrate':
        return 'This card attributes no delivery, so there is nothing to integrate.';
      default:
        return 'Containment has not been answered for this card yet. An absent answer is not a negative one.';
    }
  });

  /** `null` when there is no repository evidence to roll up: not checked, not "no". */
  readonly released = computed<boolean | null>(() => {
    const answer = this.answer();
    if (answer) return answer.released;
    return this.job().integration?.released ?? null;
  });

  /**
   * Release membership has its own quiet scale. A delivery that is in the
   * integration branch but not yet in the release line is the ordinary case
   * between two releases, not a finding, so it never borrows the acute
   * treatment the integration row uses.
   */
  readonly releaseState = computed<'released' | 'unreleased' | 'unknown'>(() => {
    const released = this.released();
    if (released === null) return 'unknown';
    return released ? 'released' : 'unreleased';
  });

  readonly releaseLabel = computed(() => {
    const released = this.released();
    if (released === null) return 'Release not checked';
    return released ? `In ${this.releaseBranch()}` : `Not in ${this.releaseBranch()}`;
  });

  readonly mergeCommit = computed(() => this.answer()?.mergeCommit ?? null);

  readonly mergeLabel = computed(() => {
    const sha = this.mergeCommit();
    return sha ? sha.slice(0, 9) : null;
  });

  readonly mergeTooltip = computed(
    () => this.answer()?.mergeSubject ?? 'The merge that carried this delivery.',
  );

  /**
   * The supersede marker, next to the delivery statement instead of only in a
   * collapsed block in the Git tab. `replacement-pending` is deliberately
   * phrased as pending: it means "requeued, replacement not published yet",
   * not "replaced by this".
   */
  readonly supersession = computed<'replacement-pending' | 'replaced' | null>(() => {
    const commits = this.answer()?.commits ?? [];
    if (commits.some((commit) => commit.supersession === 'replacement-pending')) {
      return 'replacement-pending';
    }
    if (commits.length > 0 && commits.every((commit) => commit.supersession === 'replaced')) {
      return 'replaced';
    }
    return null;
  });

  readonly supersessionLabel = computed(() => {
    switch (this.supersession()) {
      case 'replacement-pending': return 'Replacement pending';
      case 'replaced': return 'Delivery replaced';
      default: return null;
    }
  });

  readonly supersessionTooltip = computed(() => {
    switch (this.supersession()) {
      case 'replacement-pending':
        return 'This delivery was requeued and the replacement attempt has not published yet. '
          + 'It is still the delivery this card has.';
      case 'replaced':
        return 'Every delivery attributed to this card has a named successor.';
      default:
        return '';
    }
  });

  readonly claim = computed<TaskCompletionClaim | null>(
    () => this.answer()?.completionClaim ?? this.job().completionClaim ?? null,
  );

  readonly claimLabel = computed(() => {
    switch (this.claim()?.basis) {
      case 'integrated-delivery': return 'Completed on a contained delivery';
      case 'deliverable-without-code': return 'Completed on a deliverable without code';
      case 'operator-override': return 'Completed by operator override';
      default: return null;
    }
  });

  /** An override's written reason is shown wherever the card claims completion. */
  readonly overrideReason = computed(() =>
    this.claim()?.basis === 'operator-override' ? this.claim()?.reason?.trim() || null : null,
  );

  readonly show = computed(() => this.inDeliveredLane()
    && (!!this.answer() || !!this.job().integration || !!this.job().completionClaim));
}
