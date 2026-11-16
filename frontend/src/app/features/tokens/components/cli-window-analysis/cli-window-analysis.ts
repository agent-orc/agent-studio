import { SegmentedControlComponent } from '../../../../components/segmented-control/segmented-control.component';
import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal } from '@angular/core';
import type { CliUsageQuotaRow } from '../../services/cli-usage.store';
import type { AdHocUsageAggregate, TokenSummaryAggregate, TokenTimeline } from '../../models/tokens.model';
import { AppTooltipDirective } from '../../../../components/tooltip/app-tooltip.directive';
import { TokensApiService } from '../../services/tokens-api.service';
import type { SubscriptionWeek } from '../../models/subscription-week.model';

type AnalysisPeriod = '1h' | '24h' | '7d';

interface TrendPoint { label: string; total: number; height: number; }
interface StreamPart { label: string; tokens: number; pct: number; }

@Component({
  selector: 'app-cli-window-analysis',
  standalone: true,
  imports: [SegmentedControlComponent, AppTooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './cli-window-analysis.html',
  styleUrl: './cli-window-analysis.scss',
})
export class CliWindowAnalysisComponent {
  private readonly api = inject(TokensApiService);
  readonly cliType = input.required<'claude' | 'codex'>();
  readonly quotaRows = input<CliUsageQuotaRow[]>([]);
  readonly tokens = input<TokenSummaryAggregate | null>(null);
  readonly adhoc = input<AdHocUsageAggregate | null>(null);
  readonly timeline24h = input<TokenTimeline | null>(null);
  readonly timeline7d = input<TokenTimeline | null>(null);
  readonly refreshing = input(false);
  readonly refresh = output<void>();
  /** Card analysis carries the split by model and reasoning level. */
  readonly openAnalysis = output<void>();

  readonly period = signal<AnalysisPeriod>('24h');
  readonly selectedSubscriptionKey = signal<string | null>(null);
  readonly periods: readonly AnalysisPeriod[] = ['1h', '24h', '7d'];
  readonly segmentedPeriods = this.periods.map(value => ({ value, label: value, testid: 'cli-window-period-' + value }));
  readonly accounts = computed(() => this.quotaRows().filter(row => row.cliType === this.cliType()));
  readonly row = computed<CliUsageQuotaRow | null>(() => {
    const accounts = this.accounts();
    if (accounts.length === 0) return null;
    return accounts.find(row => row.subscriptionKey === this.selectedSubscriptionKey()) ?? accounts[0];
  });
  readonly subscriptionKey = computed(() => this.row()?.subscriptionKey ?? null);
  readonly subscriptionWeek = signal<SubscriptionWeek | null>(null);
  readonly subscriptionWeekError = signal(false);
  private readonly loadWeek = effect(onCleanup => {
    const cli = this.cliType();
    const key = this.subscriptionKey();
    this.subscriptionWeek.set(null);
    this.subscriptionWeekError.set(false);
    const request = this.api.getSubscriptionWeek(cli, key).subscribe({
      next: value => this.subscriptionWeek.set(value),
      error: () => this.subscriptionWeekError.set(true),
    });
    onCleanup(() => request.unsubscribe());
  });
  readonly label = computed(() => this.cliType() === 'claude' ? 'Claude' : 'Codex');
  readonly cliModels = computed(() => (this.tokens()?.byModel ?? []).filter(row => this.modelMatches(row.model)));
  readonly capturedTokens = computed(() => this.cliModels().reduce((sum, model) => sum + this.modelTotal(model), 0)
    + (this.cliType() === 'claude' ? this.adhocTotal() : 0));
  readonly streamParts = computed<StreamPart[]>(() => {
    let input = 0, output = 0, cache = 0;
    for (const model of this.cliModels()) {
      input += model.inputTokens;
      output += model.outputTokens;
      cache += model.cacheReadTokens + model.cacheCreationTokens;
    }
    if (this.cliType() === 'claude' && this.adhoc()) {
      input += this.adhoc()!.inputTokens;
      output += this.adhoc()!.outputTokens;
      cache += this.adhoc()!.cacheReadTokens + this.adhoc()!.cacheCreationTokens;
    }
    const total = Math.max(1, input + output + cache);
    return [
      { label: 'Input', tokens: input, pct: input / total * 100 },
      { label: 'Output', tokens: output, pct: output / total * 100 },
      { label: 'Cache', tokens: cache, pct: cache / total * 100 },
    ];
  });
  readonly trend = computed<TrendPoint[]>(() => {
    const timeline = this.period() === '7d' ? this.timeline7d() : this.timeline24h();
    if (!timeline) return [];
    const hours = this.period() === '1h' ? 1 : this.period() === '24h' ? 24 : 168;
    const start = Date.parse(timeline.windowEnd) - hours * 3_600_000;
    const buckets = new Map<string, number>();
    for (const cell of timeline.cells) {
      if (Date.parse(cell.bucketStart) < start) continue;
      buckets.set(cell.bucketStart, (buckets.get(cell.bucketStart) ?? 0) + cell.total);
    }
    const values = [...buckets.entries()];
    const max = Math.max(1, ...values.map(([, total]) => total));
    return values.map(([label, total]) => ({ label, total, height: Math.max(3, total / max * 100) }));
  });

  windowBurn(window: { usedPct: number | null; resetAt: string | null }, durationHours: number): number | null {
    if (window.usedPct == null || !window.resetAt) return null;
    const remaining = Math.max(0, Date.parse(window.resetAt) - Date.now());
    const elapsed = durationHours * 3_600_000 - remaining;
    return elapsed > 0 ? window.usedPct / (elapsed / 3_600_000) : null;
  }
  projected(window: { usedPct: number | null; resetAt: string | null }, durationHours: number): number | null {
    const burn = this.windowBurn(window, durationHours);
    return burn == null ? null : Math.min(999, burn * durationHours);
  }
  windowHours(label: string): number {
    const match = /([0-9]+)\s*h/i.exec(label);
    if (match) return Number(match[1]);
    return /week|7\s*d/i.test(label) ? 168 : 24;
  }
  windowPeriod(window: { label: string; resetAt: string | null; observedStartAt?: string | null }): string | null {
    if (!/week|7\s*d/i.test(window.label) || !window.resetAt) return null;
    const end = Date.parse(window.resetAt);
    if (!Number.isFinite(end)) return null;
    return `${this.formatUtc(window.observedStartAt ?? new Date(end - 7 * 86_400_000).toISOString())} to ${this.formatUtc(window.resetAt)}`;
  }
  formatTokens(value: number): string {
    if (value < 1_000) return value.toLocaleString('en-US');
    if (value < 1_000_000) return `${(value / 1_000).toFixed(value < 10_000 ? 1 : 0)}K`;
    return `${(value / 1_000_000).toFixed(2)}M`;
  }
  formatPct(value: number | null): string { return value == null ? 'Unknown' : `${value.toFixed(1)}% / h`; }
  capturedShare(tokens: number, total: number): string { return total > 0 ? `${(tokens / total * 100).toFixed(1)}%` : '0%'; }
  selectSubscription(event: Event): void {
    this.selectedSubscriptionKey.set((event.target as HTMLSelectElement).value || null);
  }
  formatUtc(value: string | null): string { return value ? new Date(value).toLocaleString('en-GB', { timeZone: 'UTC', dateStyle: 'medium', timeStyle: 'short' }) + ' UTC' : 'Unknown'; }

  private adhocTotal(): number {
    const value = this.adhoc();
    return value ? value.inputTokens + value.outputTokens + value.cacheReadTokens + value.cacheCreationTokens : 0;
  }
  private modelTotal(value: { inputTokens: number; outputTokens: number; cacheReadTokens: number; cacheCreationTokens: number }): number {
    return value.inputTokens + value.outputTokens + value.cacheReadTokens + value.cacheCreationTokens;
  }
  private modelMatches(model: string): boolean {
    const value = model.toLowerCase();
    return this.cliType() === 'claude'
      ? /claude|haiku|sonnet|opus/.test(value)
      : /codex|^gpt|^o[0-9]/.test(value);
  }
}
