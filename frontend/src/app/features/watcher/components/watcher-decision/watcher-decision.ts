import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { WatcherApiService } from '../../services/watcher-api.service';
import type { WatcherDecisionState, WatcherProposal } from '../../models/watcher.model';
import { WATCHER_PARTICIPANT_ID, WATCHER_TOPICS } from '../../models/watcher.model';

/** The answer the operator is composing. Null means no form is open. */
type PendingAnswer = Exclude<WatcherDecisionState, 'pending'> | null;

/**
 * Review mode for one Watcher proposal, rendered under its Decision entry in
 * Activity across projects.
 *
 * A proposal never enters Ready by itself; this surface is the only path from
 * the proposal state into the run queue, and every answer is attributable.
 * Approve promotes the card with the recommended model, edit does the same but
 * is counted separately as evidence for the auto-approval promotion rule,
 * merge folds the finding into an existing card, and reject needs a reason
 * because that reason becomes a visible, expiring suppression.
 */
@Component({
  selector: 'app-watcher-decision',
  standalone: true,
  imports: [FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './watcher-decision.html',
  styleUrl: './watcher-decision.scss',
})
export class WatcherDecisionComponent {
  private readonly api = inject(WatcherApiService);

  /** Durable case id, taken from the feed row's bus correlation id. */
  readonly caseId = input.required<string>();
  readonly decided = output<WatcherProposal>();

  readonly proposal = signal<WatcherProposal | null>(null);
  readonly loading = signal(false);
  readonly submitting = signal(false);
  readonly error = signal<string | null>(null);
  readonly answer = signal<PendingAnswer>(null);

  // Signals, not plain fields: the confirm button is a computed over them, and
  // a plain field would never re-run it as the operator types.
  readonly reasonDraft = signal('');
  readonly mergeTargetDraft = signal('');

  /** True when the load finished and no proposal exists for this case. */
  readonly missing = computed(() => !this.loading() && this.proposal() === null && this.error() === null);

  private loadedCaseId: string | null = null;

  readonly pending = computed(() => this.proposal()?.decision.state === 'pending');
  readonly targetCard = computed(() => {
    const proposal = this.proposal();
    return proposal?.createdTaskKey ?? proposal?.commentedOnTaskKey ?? null;
  });
  readonly reasonRequired = computed(() => this.answer() === 'rejected');
  readonly mergeTargetRequired = computed(() => this.answer() === 'merged');
  readonly canSubmit = computed(() => {
    if (this.submitting()) return false;
    if (this.reasonRequired()) return this.reasonDraft().trim().length > 0;
    if (this.mergeTargetRequired()) return this.mergeTargetDraft().trim().length > 0;
    return this.answer() !== null;
  });

  constructor() {
    // The feed reuses one instance while the operator clicks through rows, so
    // the load follows the case id rather than the component lifetime.
    effect(() => this.load(this.caseId()));
  }

  start(answer: Exclude<WatcherDecisionState, 'pending'>): void {
    this.answer.set(answer);
    this.error.set(null);
  }

  cancel(): void {
    this.answer.set(null);
    this.reasonDraft.set('');
    this.mergeTargetDraft.set('');
  }

  submit(): void {
    const proposal = this.proposal();
    const answer = this.answer();
    if (!proposal || !answer || !this.canSubmit()) return;

    this.submitting.set(true);
    this.error.set(null);
    this.api
      .decide(proposal.id, {
        decision: answer,
        reason: this.reasonDraft().trim() || null,
        mergeIntoTaskKey: this.mergeTargetDraft().trim() || null,
      })
      .subscribe({
        next: decided => {
          this.submitting.set(false);
          this.proposal.set(decided);
          this.cancel();
          this.decided.emit(decided);
        },
        error: response => {
          this.submitting.set(false);
          this.error.set(response?.error?.error ?? 'The decision could not be recorded.');
        },
      });
  }

  decisionLabel(state: WatcherDecisionState): string {
    return state === 'approved' ? 'Approved'
      : state === 'edited' ? 'Approved after an edit'
      : state === 'merged' ? 'Merged into another card'
      : state === 'rejected' ? 'Rejected'
      : 'Awaiting a decision';
  }

  private load(caseId: string): void {
    if (!caseId || caseId === this.loadedCaseId) return;
    this.loadedCaseId = caseId;
    this.proposal.set(null);
    this.cancel();
    this.loading.set(true);
    this.error.set(null);
    this.api.proposals().subscribe({
      next: response => {
        this.loading.set(false);
        this.proposal.set(
          response.proposals.find(item => item.caseId === caseId) ?? null
        );
      },
      error: () => {
        this.loading.set(false);
        this.error.set('The Watcher proposal could not be loaded.');
      },
    });
  }
}

/**
 * True when a feed row is a Watcher decision the operator can answer here.
 * Kept next to the component so the feed does not restate the topic contract.
 */
export function isWatcherDecisionEntry(entry: {
  participantId?: string | null;
  topic?: string | null;
  correlationId?: string | null;
}): boolean {
  return entry.participantId === WATCHER_PARTICIPANT_ID
    && entry.topic === WATCHER_TOPICS.decisionRequired
    && !!entry.correlationId;
}
