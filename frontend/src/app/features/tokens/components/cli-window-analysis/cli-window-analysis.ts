import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import type { CliUsageQuotaRow } from '../../services/cli-usage.store';
import type { AdHocUsageAggregate, TokenSummaryAggregate, TokenTimeline } from '../../models/tokens.model';
import { AppTooltipDirective } from '../../../../components/tooltip/app-tooltip.directive';
import { formatUsageTokens } from '../../usage-number-format.util';

type AnalysisPeriod = '1h' | '24h' | '7d';

interface TrendPoint { label: string; total: number; height: number; }
interface StreamPart { label: string; tokens: number; pct: number; }

@Component({
  selector: 'app-cli-window-analysis',
  standalone: true,
  imports: [AppTooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './cli-window-analysis.html',
  styleUrl: './cli-window-analysis.scss',
})
export class CliWindowAnalysisComponent {
  readonly cliType = input.required<'claude' | 'codex'>();
  readonly quotaRows = input<CliUsageQuotaRow[]>([]);
  readonly tokens = input<TokenSummaryAggregate | null>(null);
  readonly adhoc = input<AdHocUsageAggregate | null>(null);
  readonly timeline24h = input<TokenTimeline | null>(null);
  readonly timeline7d = input<TokenTimeline | null>(null);
  readonly refreshing = input(false);
  readonly refresh = output<void>();

  readonly period = signal<AnalysisPeriod>('24h');
  readonly periods: readonly AnalysisPeriod[] = ['1h', '24h', '7d'];
  readonly row = computed(() => this.quotaRows().find(row => row.cliType === this.cliType()) ?? null);
  readonly label = computed(() => this.cliType() === 'claude' ? 'Claude' : 'Codex');
  readonly cliModels = computed(() =>
    (this.tokens()?.byModel ?? []).filter(row => this.modelMatches(row.modelId ?? row.model)));
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
  readonly formatTokens = formatUsageTokens;
  formatPct(value: number | null): string { return value == null ? 'Unknown' : `${value.toFixed(1)}% / h`; }

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
