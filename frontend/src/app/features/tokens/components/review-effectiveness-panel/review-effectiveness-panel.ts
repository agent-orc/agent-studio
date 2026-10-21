import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import type { Subscription } from 'rxjs';
import type {
  ReviewEffectivenessRow,
  ReviewEffectivenessSummary,
  ReviewLevel,
} from '../../models/review-effectiveness.model';
import { ReviewEffectivenessApiService } from '../../services/review-effectiveness-api.service';
import { formatCompactTokens, formatCompactUsd } from '../../token-number-format.util';

type LevelFilter = ReviewLevel | 'all';

const LEVEL_LABELS: Record<ReviewLevel, string> = {
  aspect: 'Aspect',
  grade: 'Grade',
  deep: 'Deep review',
};

/**
 * Review effectiveness per review level, model and reasoning level
 * (AGT-3269). Cost per review, blocker rate, miss rate and cost per real
 * finding come from the backend aggregate; a missing price or denominator
 * renders as "n/a", never as zero.
 */
@Component({
  selector: 'app-review-effectiveness-panel',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './review-effectiveness-panel.html',
  styleUrl: './review-effectiveness-panel.scss',
})
export class ReviewEffectivenessPanelComponent implements OnInit {
  private readonly api = inject(ReviewEffectivenessApiService);
  private readonly destroyRef = inject(DestroyRef);
  private request: Subscription | null = null;

  readonly windows = [7, 30, 90] as const;
  readonly levels: readonly LevelFilter[] = ['all', 'aspect', 'grade', 'deep'];

  readonly days = signal<number>(30);
  readonly level = signal<LevelFilter>('all');
  readonly summary = signal<ReviewEffectivenessSummary | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  readonly rows = computed(() => {
    const level = this.level();
    const rows = this.summary()?.rows ?? [];
    return level === 'all' ? rows : rows.filter(row => row.level === level);
  });

  readonly exportUrl = computed(() => this.api.exportUrl(this.days()));

  readonly formatUsd = formatCompactUsd;
  readonly formatTokens = formatCompactTokens;

  ngOnInit(): void {
    this.load();
  }

  setDays(days: number): void {
    if (days === this.days()) return;
    this.days.set(days);
    this.load();
  }

  setLevel(level: LevelFilter): void {
    this.level.set(level);
  }

  levelLabel(level: LevelFilter): string {
    return level === 'all' ? 'All levels' : LEVEL_LABELS[level];
  }

  rowKey(row: ReviewEffectivenessRow): string {
    return `${row.level}|${row.model}|${row.reasoningLevel}`;
  }

  percent(value: number | null): string {
    return value === null ? 'n/a' : `${(value * 100).toFixed(1)}%`;
  }

  usd(value: number | null): string {
    return value === null ? 'n/a' : formatCompactUsd(value);
  }

  totalTokens(row: ReviewEffectivenessRow): number {
    return row.inputTokens + row.outputTokens + row.cacheReadTokens + row.cacheCreationTokens;
  }

  duration(ms: number | null): string {
    if (ms === null) return 'n/a';
    const seconds = Math.round(ms / 1000);
    return seconds < 120 ? `${seconds}s` : `${Math.round(seconds / 60)}m`;
  }

  missSources(row: ReviewEffectivenessRow): string {
    const entries = Object.entries(row.missesBySource);
    return entries.length === 0 ? 'No misses recorded' : entries.map(([source, count]) => `${source}: ${count}`).join(', ');
  }

  private load(): void {
    this.request?.unsubscribe();
    this.loading.set(true);
    this.error.set(null);
    this.request = this.api.getSummary(this.days())
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: summary => {
          this.summary.set(summary);
          this.loading.set(false);
        },
        error: () => {
          this.error.set('Review effectiveness could not be loaded.');
          this.loading.set(false);
        },
      });
  }
}
