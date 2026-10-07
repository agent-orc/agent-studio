import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
} from '@angular/core';

import type { TaskInfo } from '../../../../models/task.model';
import type { CodeReviewListEntry } from '../../../../services/task.service';
import { TaskService } from '../../../../services/task.service';
import { FileSourceHistoryComponent } from '../../../../components/file-source-history/file-source-history.component';
import { formatDateTimeUtc } from '../../../../services/format.util';
import { DisclosureMarkerComponent } from '../../../../components/disclosure-marker/disclosure-marker.component';
import { CouncilReviewReactionComponent } from '../protocol-pane/council-review-reaction/council-review-reaction.component';
import type {
  EscalationGateItem,
  EscalationGateSource,
  EscalationReissue,
} from '../escalation-summary/escalation-summary.util';
import { parseFollowUp } from './follow-up-parser';

interface ReviewRound {
  entry: CodeReviewListEntry;
  number: number;
}

@Component({
  selector: 'app-escalation-details',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FileSourceHistoryComponent, CouncilReviewReactionComponent, DisclosureMarkerComponent],
  templateUrl: './escalation-details.component.html',
  styleUrl: './escalation-details.component.scss',
})
export class EscalationDetailsComponent {
  readonly job = input.required<TaskInfo>();
  readonly reviews = input<readonly CodeReviewListEntry[]>([]);
  readonly followUpMarkdown = input<string | null>(null);
  readonly gateItems = input<readonly EscalationGateItem[]>([]);
  readonly gateSource = input<EscalationGateSource>('none');
  readonly reissues = input<readonly EscalationReissue[]>([]);
  readonly followUpMaximized = signal(false);
  readonly disclosureVersion = signal(0);
  readonly parsedFollowUp = computed(() => parseFollowUp(this.followUpMarkdown() ?? ''));

  private readonly jobs = inject(TaskService);
  private readonly reviewBodies = signal<Record<string, string | null>>({});
  private readonly requestedFiles = new Set<string>();
  private loadedJobId: string | null = null;

  readonly openFindingCount = computed(
    () => this.gateItems().filter((item) => !item.checked).length,
  );

  readonly reviewRounds = computed<ReviewRound[]>(() =>
    [...this.reviews()]
      .filter((entry) => !!entry.grade?.trim() || /^code-review-grade-/i.test(entry.fileName))
      .sort((a, b) => (a.runAt ?? '').localeCompare(b.runAt ?? ''))
      .map((entry, index) => ({ entry, number: index + 1 }))
      .reverse(),
  );

  readonly hasCouncilContext = computed(
    () => !!this.followUpMarkdown() || this.reviewRounds().some((round) => !!round.entry.councilReaction),
  );

  constructor() {
    effect(() => {
      const jobId = this.job().id;
      if (jobId === this.loadedJobId) return;
      this.loadedJobId = jobId;
      this.reviewBodies.set({});
      this.requestedFiles.clear();
    });
  }

  loadReviewBody(round: ReviewRound, event: Event): void {
    const details = event.currentTarget as HTMLDetailsElement;
    const fileName = round.entry.fileName;
    if (!details.open || this.requestedFiles.has(fileName)) return;
    this.requestedFiles.add(fileName);
    const job = this.job();
    this.jobs.readJobFile(job.id, fileName, job.watchPath).subscribe({
      next: (body) => this.reviewBodies.update((current) => ({ ...current, [fileName]: body })),
      error: () => this.reviewBodies.update((current) => ({ ...current, [fileName]: null })),
    });
  }

  reviewBody(fileName: string): string | null | undefined {
    const bodies = this.reviewBodies();
    return Object.prototype.hasOwnProperty.call(bodies, fileName) ? bodies[fileName] : undefined;
  }

  gateSourceLabel(): string {
    switch (this.gateSource()) {
      case 'council-reaction': return 'Council reaction';
      case 'gate-evidence': return 'Completion-gate findings';
      case 'review-evidence': return 'Recorded review evidence';
      default: return 'No structured source';
    }
  }

  formatRunAt(iso: string | null | undefined): string {
    return iso ? formatDateTimeUtc(iso) : '';
  }

  copy(value: string): void {
    void navigator.clipboard?.writeText(value);
  }

  disclosureChanged(): void {
    this.disclosureVersion.update(value => value + 1);
  }
}
