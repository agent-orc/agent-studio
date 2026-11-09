import { ChangeDetectionStrategy, Component, computed, inject, input, output } from '@angular/core';
import { BulkLaneActionsComponent } from '../../../board';
import { laneName } from '../../../../models/lane-presentation';
import type { TaskInfo } from '../../../../models/task.model';
import { TaskService } from '../../../../services/task.service';
import type { OrchestratorLogEntry } from '../../models/orchestrator.model';
import type { FeedBurst } from '../orchestrator-feed/feed-bursts';

interface BurstCard {
  readonly key: string;
  readonly entry: OrchestratorLogEntry;
  readonly task: TaskInfo | null;
}

/**
 * AGT-3585: detail pane for a burst in Activity across projects. Lists every
 * card the burst touched with its current lane and offers the bulk actions
 * that are valid for all of them.
 */
@Component({
  selector: 'app-orchestrator-feed-burst-detail',
  standalone: true,
  imports: [BulkLaneActionsComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './orchestrator-feed-burst-detail.html',
  styleUrl: './orchestrator-feed-burst-detail.scss',
})
export class OrchestratorFeedBurstDetailComponent {
  private readonly taskService = inject(TaskService);

  readonly burst = input.required<FeedBurst>();
  readonly openTaskRequest = output<OrchestratorLogEntry>();

  /** One row per distinct card, in feed order (newest first). */
  readonly cards = computed<BurstCard[]>(() => {
    const jobs = this.taskService.jobs();
    const seen = new Set<string>();
    const cards: BurstCard[] = [];
    for (const entry of this.burst().entries) {
      const key = `${entry.watchPath ?? ''}\u0000${entry.jobId ?? ''}`;
      if (!entry.jobId || seen.has(key)) continue;
      seen.add(key);
      const task = jobs.find(job => job.id === entry.jobId && (!entry.watchPath || job.watchPath === entry.watchPath)) ?? null;
      cards.push({ key, entry, task });
    }
    return cards;
  });
  readonly onBoard = computed(() => this.cards().flatMap(card => card.task ? [card.task] : []));
  readonly missing = computed(() => this.cards().length - this.onBoard().length);

  cardKey(card: BurstCard): string {
    return card.task?.key || card.task?.displayKey || card.entry.jobId || '';
  }

  laneOf(card: BurstCard): string {
    return card.task ? laneName(card.task.state) : 'Not on the board';
  }

  kindLabel(kind: string): string {
    return kind.charAt(0).toUpperCase() + kind.slice(1);
  }

  formatTime(iso: string): string {
    const date = new Date(iso);
    return Number.isNaN(date.getTime()) ? iso : date.toLocaleString('en-US');
  }
}
