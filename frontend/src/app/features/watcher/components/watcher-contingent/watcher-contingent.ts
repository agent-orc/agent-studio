import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { WatcherApiService } from '../../services/watcher-api.service';
import type { WatcherStatus } from '../../models/watcher.model';

/** One budget dimension as the strip renders it. */
export interface ContingentRow {
  /** Stable identity for tracking; also the test hook. */
  id: string;
  label: string;
  window: 'day' | 'week';
  used: number;
  /** Null means the operator declared no ceiling for this dimension. */
  limit: number | null;
  usedPct: number | null;
  exhausted: boolean;
}

/**
 * The Watcher contingent next to the usage caps on Workspace CLI Management:
 * the dedicated per-day and per-week budget, what has been spent, and how many
 * cases are waiting because the budget ran out.
 *
 * The backlog number is the point of the strip. When the contingent is used up
 * the Watcher keeps detecting and counting; only spending stops. An operator
 * needs to see that difference to decide whether to raise the budget.
 */
@Component({
  selector: 'app-watcher-contingent',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './watcher-contingent.html',
  styleUrl: './watcher-contingent.scss',
})
export class WatcherContingentComponent {
  private readonly api = inject(WatcherApiService);

  readonly status = signal<WatcherStatus | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  readonly rows = computed<ContingentRow[]>(() => {
    const contingent = this.status()?.contingent;
    if (!contingent) return [];
    const { limits, usage } = contingent;
    return [
      this.row('model-calls-day', 'Model calls', 'day', usage.modelCallsDay, limits.modelCallsPerDay),
      this.row('model-calls-week', 'Model calls', 'week', usage.modelCallsWeek, limits.modelCallsPerWeek),
      this.row('tokens-day', 'Tokens', 'day', usage.tokensDay, limits.tokensPerDay),
      this.row('tokens-week', 'Tokens', 'week', usage.tokensWeek, limits.tokensPerWeek),
      this.row('proposals-day', 'Proposals', 'day', usage.proposalsDay, limits.proposalsPerDay),
      this.row('proposals-week', 'Proposals', 'week', usage.proposalsWeek, limits.proposalsPerWeek),
      this.row('comments-day', 'Comments', 'day', usage.commentsDay, limits.commentsPerDay),
      this.row('comments-week', 'Comments', 'week', usage.commentsWeek, limits.commentsPerWeek),
    ];
  });

  readonly backlog = computed(() => this.status()?.contingent.backlogCases ?? 0);
  readonly exhausted = computed(() => (this.status()?.contingent.exhaustedDimensions ?? []).length > 0);

  /** Cost of the day, or "unknown" when a call in the window had no price. */
  readonly costToday = computed(() => {
    const contingent = this.status()?.contingent;
    if (!contingent) return '-';
    return contingent.costUsdDayDisplay === 'unknown'
      ? 'unknown'
      : `$${contingent.costUsdDayDisplay}`;
  });

  constructor() {
    this.reload();
  }

  reload(): void {
    this.loading.set(true);
    this.error.set(null);
    this.api.status().subscribe({
      next: status => {
        this.loading.set(false);
        this.status.set(status);
      },
      error: () => {
        this.loading.set(false);
        this.error.set('The Watcher contingent could not be loaded.');
      },
    });
  }

  barWidth(usedPct: number | null): number {
    if (usedPct === null) return 0;
    return Math.min(100, Math.max(0, usedPct));
  }

  format(value: number): string {
    return value.toLocaleString();
  }

  private row(
    id: string,
    label: string,
    window: 'day' | 'week',
    used: number,
    limit: number | null
  ): ContingentRow {
    // A zero ceiling is a closed dimension, not a division by zero: it is
    // rendered as fully consumed so "off" reads as off.
    const usedPct = limit === null ? null : limit === 0 ? 100 : (used / limit) * 100;
    return {
      id,
      label,
      window,
      used,
      limit,
      usedPct,
      exhausted: limit !== null && used >= limit,
    };
  }
}
