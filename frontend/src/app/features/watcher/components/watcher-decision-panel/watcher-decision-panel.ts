import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { WatcherApiService } from '../../services/watcher-api.service';
import type { WatcherProposal, WatcherProposalOutcome } from '../../models/watcher.model';
import { StatusIndicatorComponent, type StatusTone } from '../../../../components/status-indicator/status-indicator.component';

/**
 * A recorded proposal decision on the shared indicator. Approval and edited
 * acceptance are the wanted outcome; a merge into an existing card is a fact
 * worth noticing; a rejection is a settled operator choice, not a failure (R4).
 */
export const WATCHER_OUTCOME_TONE: Readonly<Record<WatcherProposalOutcome, StatusTone>> = {
  approved: 'success',
  edited: 'success',
  merged: 'info',
  rejected: 'neutral',
};

export function watcherOutcomeTone(outcome: string): StatusTone {
  return Object.hasOwn(WATCHER_OUTCOME_TONE, outcome) ? WATCHER_OUTCOME_TONE[outcome as WatcherProposalOutcome] : 'neutral';
}

/** Minimal shape this panel needs from an Activity feed entry - kept decoupled from the orchestrator feature's own model type. */
export interface WatcherDecisionEntry {
  ts: string;
  jobId?: string | null;
}

/**
 * Review-mode Decision panel (orchestrator-waechter dossier §10.4): renders
 * inside the Activity feed detail pane for an entry whose topic is
 * `watcher-decision-required`. Shows the recommended model and the four
 * decision affordances - approve, edited-accept, merge into an existing
 * card, or reject with a reason that feeds the suppression list. A proposal
 * never enters Ready by itself; every lane change here is the direct result
 * of one of these buttons.
 */
@Component({
  selector: 'app-watcher-decision-panel',
  standalone: true,
  imports: [FormsModule, StatusIndicatorComponent],
  templateUrl: './watcher-decision-panel.html',
  styleUrl: './watcher-decision-panel.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class WatcherDecisionPanelComponent implements OnInit {
  readonly entry = input.required<WatcherDecisionEntry>();
  readonly decided = output<void>();
  readonly outcomeTone = watcherOutcomeTone;

  private readonly watcherApi = inject(WatcherApiService);

  private readonly proposals = signal<WatcherProposal[]>([]);
  readonly proposal = computed<WatcherProposal | null>(() => {
    const jobId = this.entry().jobId;
    if (!jobId) return null;
    return this.proposals().find(p => p.jobId === jobId || p.commentedJobId === jobId) ?? null;
  });

  readonly decidingProposalId = signal<string | null>(null);
  readonly decisionError = signal<string | null>(null);
  readonly rejecting = signal(false);
  readonly merging = signal(false);
  reasonDraft = '';
  mergeTargetDraft = '';

  ngOnInit(): void {
    this.load();
  }

  private load(): void {
    this.watcherApi.proposals(true).subscribe({
      next: (r) => this.proposals.set(r.proposals),
      error: () => { /* Best-effort: panel just shows no recommendation until the next refresh. */ },
    });
  }

  startReject(): void {
    this.rejecting.set(true);
    this.merging.set(false);
    this.reasonDraft = '';
  }

  startMerge(): void {
    this.merging.set(true);
    this.rejecting.set(false);
    this.mergeTargetDraft = '';
  }

  cancel(): void {
    this.rejecting.set(false);
    this.merging.set(false);
    this.reasonDraft = '';
    this.mergeTargetDraft = '';
  }

  decide(outcome: WatcherProposalOutcome): void {
    const proposal = this.proposal();
    if (!proposal) return;
    if (outcome === 'rejected' && !this.reasonDraft.trim()) return;
    if (outcome === 'merged' && !this.mergeTargetDraft.trim()) return;

    this.decidingProposalId.set(proposal.id);
    this.decisionError.set(null);
    this.watcherApi.decide(proposal.id, {
      outcome,
      reason: outcome === 'rejected' ? this.reasonDraft.trim() : null,
      mergedIntoJobId: outcome === 'merged' ? this.mergeTargetDraft.trim() : null,
    }).subscribe({
      next: () => {
        this.decidingProposalId.set(null);
        this.cancel();
        this.load();
        this.decided.emit();
      },
      error: (err) => {
        this.decidingProposalId.set(null);
        this.decisionError.set(err?.error?.error || err?.message || 'Decision failed');
      },
    });
  }
}
