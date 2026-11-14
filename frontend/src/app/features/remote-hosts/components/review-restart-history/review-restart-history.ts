import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import {
  TaskReferenceMicrocardComponent,
  type TaskReferenceStatus,
} from '../../../../components/task-reference-microcard/task-reference-microcard';
import { TaskReferenceNavigationService } from '../../../../services/task-reference-navigation.service';
import { TaskService } from '../../../../services/task.service';
import type { RemoteHost, ReviewRestartLostAttempt } from '../../models/remote-host.model';

export interface ReviewRestartRow {
  id: string;
  runner: string;
  restartedAt: string;
  instanceId: string;
  reviewsLost: number;
  lostAttempts: readonly ReviewRestartLostAttempt[];
}

/**
 * AGT-3690: every review daemon restart of the shown host's roles, newest
 * first, with the review attempts each one lost and a link to their cards.
 * The host fact line keeps only the newest restart, and a later clean restart
 * used to overwrite "1 reviews lost" with "0 reviews lost". History renders
 * quietly (R4): the acute signal is on the card that lost its review.
 */
@Component({
  selector: 'app-review-restart-history',
  standalone: true,
  imports: [DatePipe, TaskReferenceMicrocardComponent],
  templateUrl: './review-restart-history.html',
  styleUrl: './review-restart-history.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ReviewRestartHistoryComponent {
  /** The host and its role identities; restarts are recorded per role. */
  readonly hosts = input<readonly RemoteHost[]>([]);
  private readonly tasks = inject(TaskService);
  private readonly navigation = inject(TaskReferenceNavigationService);
  private readonly requested = new Set<string>();
  private readonly statuses = signal<ReadonlyMap<string, TaskReferenceStatus>>(new Map());

  readonly rows = computed<ReviewRestartRow[]>(() => {
    const rows = new Map<string, ReviewRestartRow>();
    for (const host of this.hosts()) {
      for (const restart of host.reviewRestarts ?? []) {
        const id = `${host.clientId}|${restart.instanceId}|${restart.restartedAt}`;
        if (rows.has(id)) continue;
        rows.set(id, {
          id,
          runner: host.name,
          restartedAt: restart.restartedAt,
          instanceId: restart.instanceId,
          reviewsLost: restart.reviewsLost,
          lostAttempts: restart.lostAttempts ?? [],
        });
      }
    }
    return [...rows.values()].sort((left, right) =>
      Date.parse(right.restartedAt) - Date.parse(left.restartedAt) || left.id.localeCompare(right.id));
  });

  readonly lostTotal = computed(() => this.rows().reduce((sum, row) => sum + row.reviewsLost, 0));

  /** One batch per newly seen set of cards, the same projection the microcard hydrator uses. */
  private readonly loadStatuses = effect(() => {
    const keys = [...new Set(this.rows()
      .flatMap(row => row.lostAttempts.map(lost => lost.taskKey.trim().toUpperCase()))
      .filter(key => key.length > 0 && !this.requested.has(key)))];
    if (!keys.length) return;
    keys.forEach(key => this.requested.add(key));
    untracked(() => this.tasks.getReferenceStatuses(keys).subscribe({
      next: items => this.statuses.update(current => {
        const next = new Map(current);
        for (const item of items) next.set(item.key.toUpperCase(), item);
        return next;
      }),
      // A failed batch leaves the plain card link in place.
      error: () => undefined,
    }));
  });

  status(taskKey: string): TaskReferenceStatus | null {
    return this.statuses().get(taskKey.trim().toUpperCase()) ?? null;
  }

  lostLabel(count: number): string {
    return count === 0 ? 'No review lost' : count === 1 ? '1 review lost' : `${count} reviews lost`;
  }

  openCard(event: MouseEvent, taskKey: string): void {
    event.preventDefault();
    this.navigation.openReferenceOrNotify(taskKey);
  }
}
