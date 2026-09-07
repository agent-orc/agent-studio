import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { WatcherApiService } from '../../services/watcher-api.service';
import type { WatcherStatusResponse } from '../../models/watcher.model';

/**
 * Watcher contingent strip (orchestrator-waechter dossier §10.4): the
 * per-day/per-week token and count budget for the Global Orchestrator
 * Watcher, shown in Workspace CLI Management next to the CLI quota strips.
 * When a category is exhausted the Watcher keeps detecting and counting
 * cases but stops calling models or creating proposals/comments - this
 * strip is where that backlog first becomes visible.
 */
@Component({
  selector: 'app-watcher-contingent-strip',
  standalone: true,
  imports: [],
  templateUrl: './watcher-contingent-strip.html',
  styleUrl: './watcher-contingent-strip.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class WatcherContingentStripComponent implements OnInit {
  private readonly watcherApi = inject(WatcherApiService);

  readonly status = signal<WatcherStatusResponse | null>(null);
  readonly errorMsg = signal<string | null>(null);

  readonly enabled = computed(() => this.status()?.options.enabled ?? false);
  readonly contingent = computed(() => this.status()?.contingent ?? null);

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.watcherApi.status().subscribe({
      next: (s) => {
        this.status.set(s);
        this.errorMsg.set(null);
      },
      error: (err) => this.errorMsg.set(err.error?.error || err.message || 'Failed to load Watcher status'),
    });
  }

  usedPct(used: number, budget: number): number {
    if (budget <= 0) return used > 0 ? 100 : 0;
    return Math.max(0, Math.min(100, (used / budget) * 100));
  }

  formatCount(used: number, budget: number): string {
    return `${used} / ${budget}`;
  }

  formatTokens(used: number, budget: number): string {
    return `${this.compact(used)} / ${this.compact(budget)}`;
  }

  private compact(n: number): string {
    if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`;
    if (n >= 1_000) return `${(n / 1_000).toFixed(1)}k`;
    return `${n}`;
  }
}
