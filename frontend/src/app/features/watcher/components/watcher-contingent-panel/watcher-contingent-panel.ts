import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { CurrencyPipe } from '@angular/common';
import { WatcherApiService } from '../../services/watcher-api.service';
import type { WatcherContingentSnapshot } from '../../models/watcher.model';

/** One budget line: what is configured, what is used, and what is left. */
export interface ContingentRow {
  key: string;
  label: string;
  window: string;
  used: number;
  limit: number;
  usedPct: number | null;
  unit: 'tokens' | 'count';
}

/**
 * The Watcher contingent, shown in Workspace CLI Management next to the quota
 * strips (dossier §10.4). It answers two questions an operator has about a
 * budget that can stop work: how much is left, and what was found but not
 * drafted because the budget ran out.
 *
 * The backlog count is the important number. When the contingent is exhausted
 * the Watcher keeps detecting and counting, so an empty proposal inbox does
 * NOT mean an empty workspace - it means the drafts were not written. Rendering
 * the backlog is what keeps that distinction visible.
 */
@Component({
  selector: 'app-watcher-contingent-panel',
  standalone: true,
  imports: [CurrencyPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './watcher-contingent-panel.html',
  styleUrl: './watcher-contingent-panel.scss',
})
export class WatcherContingentPanelComponent implements OnInit {
  private readonly api = inject(WatcherApiService);

  readonly snapshot = signal<WatcherContingentSnapshot | null>(null);
  readonly loading = signal(false);
  readonly errorMsg = signal<string | null>(null);

  readonly exhausted = computed(() => this.snapshot()?.exhausted === true);
  readonly backlog = computed(() => this.snapshot()?.backlogCases ?? 0);

  readonly rows = computed<ContingentRow[]>(() => {
    const snap = this.snapshot();
    if (!snap) return [];
    const { budget, daily, weekly } = snap;
    return [
      this.row('proposals-day', 'Proposals', 'today', daily.proposals, budget.dailyProposals, 'count'),
      this.row('proposals-week', 'Proposals', 'this week', weekly.proposals, budget.weeklyProposals, 'count'),
      this.row('comments-day', 'Comments', 'today', daily.comments, budget.dailyComments, 'count'),
      this.row('comments-week', 'Comments', 'this week', weekly.comments, budget.weeklyComments, 'count'),
      this.row('calls-day', 'Model calls', 'today', daily.modelCalls, budget.dailyModelCalls, 'count'),
      this.row('calls-week', 'Model calls', 'this week', weekly.modelCalls, budget.weeklyModelCalls, 'count'),
      this.row('tokens-day', 'Tokens', 'today', daily.tokens, budget.dailyTokens, 'tokens'),
      this.row('tokens-week', 'Tokens', 'this week', weekly.tokens, budget.weeklyTokens, 'tokens'),
    ];
  });

  /**
   * Realized weekly cost, or null when the price catalog had no entry for a
   * model used in the window. Unknown price is shown as unknown, never as zero.
   */
  readonly weeklyCost = computed(() => {
    const snap = this.snapshot();
    if (!snap) return null;
    return snap.weeklyDollars;
  });

  ngOnInit(): void {
    this.reload();
  }

  reload(): void {
    if (this.loading()) return;
    this.loading.set(true);
    this.api.getContingent().subscribe({
      next: snapshot => {
        this.snapshot.set(snapshot);
        this.errorMsg.set(null);
        this.loading.set(false);
      },
      error: error => {
        this.errorMsg.set(error?.error?.error || error?.message || 'Failed to load the Watcher contingent');
        this.loading.set(false);
      },
    });
  }

  /** Bar fill stays visible at 0 and never overflows past a spent budget. */
  barWidth(pct: number | null): number {
    if (pct === null) return 0;
    return Math.max(0, Math.min(100, pct));
  }

  severity(pct: number | null): 'ok' | 'warn' | 'crit' {
    if (pct === null) return 'ok';
    if (pct >= 100) return 'crit';
    if (pct >= 70) return 'warn';
    return 'ok';
  }

  formatCount(value: number, unit: 'tokens' | 'count'): string {
    if (unit === 'count') return `${value}`;
    if (value >= 1_000_000) return `${(value / 1_000_000).toFixed(1)}M`;
    if (value >= 1_000) return `${Math.round(value / 1_000)}k`;
    return `${value}`;
  }

  private row(
    key: string,
    label: string,
    window: string,
    used: number,
    limit: number,
    unit: 'tokens' | 'count',
  ): ContingentRow {
    // A zero budget is a deliberate setting, not a missing one. It reads as
    // fully spent so the strip matches what the Watcher will actually do.
    const usedPct = limit <= 0 ? 100 : Math.round((used / limit) * 100);
    return { key, label, window, used, limit, usedPct, unit };
  }
}
