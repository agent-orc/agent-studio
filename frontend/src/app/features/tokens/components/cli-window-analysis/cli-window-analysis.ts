import { SegmentedControlComponent } from '../../../../components/segmented-control/segmented-control.component';
import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import type { CliUsageQuotaRow } from '../../services/cli-usage.store';
import type { TokenTimeline } from '../../models/tokens.model';
import { AppTooltipDirective } from '../../../../components/tooltip/app-tooltip.directive';
import { browserTimeZone, formatZonedWithUtc } from '../../../../services/usage-time.util';
import { UsageCockpitService } from '../../../usage-cockpit/services/usage-cockpit.service';
import { TokensApiService, type CliWindowAttribution } from '../../services/tokens-api.service';
import type { QuotaWindow } from '../../../quota';

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
  private readonly tokensApi = inject(TokensApiService);
  private readonly cockpit = inject(UsageCockpitService);
  readonly cliType = input.required<'claude' | 'codex'>();
  readonly quotaRows = input<CliUsageQuotaRow[]>([]);
  readonly timeline24h = input<TokenTimeline | null>(null);
  readonly timeline7d = input<TokenTimeline | null>(null);
  readonly refreshing = input(false);
  readonly refresh = output<void>();
  /** Card analysis carries the split by model and reasoning level. */
  readonly openAnalysis = output<void>();

  readonly period = signal<AnalysisPeriod>('24h');
  readonly periods: readonly AnalysisPeriod[] = ['1h', '24h', '7d'];
  readonly segmentedPeriods = this.periods.map(value => ({ value, label: value, testid: 'cli-window-period-' + value }));
  readonly row = computed(() => this.quotaRows().find(row => row.cliType === this.cliType()) ?? null);
  readonly label = computed(() => this.cliType() === 'claude' ? 'Claude' : 'Codex');
  readonly attributions = signal<Record<string, CliWindowAttribution | null>>({});
  private readonly windowRequestKey = computed(() => `${this.cliType()}:` +
    (this.row()?.windows ?? []).map(window => `${window.label}|${window.resetAt ?? ''}|${window.windowHours ?? ''}`).join(';'));
  private readonly fetchWindows = effect(onCleanup => {
    this.windowRequestKey();
    const row = untracked(() => this.row());
    const cli = untracked(() => this.cliType());
    this.attributions.set({});
    const subscriptions = (row?.windows ?? []).filter(window => window.windowHours != null)
      .map(window => this.tokensApi.getCliWindowAttribution(cli, window.label, window.resetAt).subscribe({
        next: value => this.attributions.update(current => ({ ...current, [this.windowKey(window)]: value })),
        error: () => this.attributions.update(current => ({ ...current, [this.windowKey(window)]: null })),
      }));
    onCleanup(() => subscriptions.forEach(subscription => subscription.unsubscribe()));
  });
  readonly primaryAttribution = computed(() => {
    const primary = this.row()?.primary ?? this.row()?.windows[0];
    return primary ? this.attribution(primary) : null;
  });
  readonly streamParts = computed<StreamPart[]>(() => {
    const value = this.primaryAttribution();
    if (!value) return [];
    const input = value.inputTokens, output = value.outputTokens;
    const cache = value.cacheReadTokens + value.cacheCreationTokens;
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

  attribution(window: QuotaWindow): CliWindowAttribution | null {
    return this.attributions()[this.windowKey(window)] ?? null;
  }
  resetTime(window: QuotaWindow): string {
    return formatZonedWithUtc(window.resetAt, this.cockpit.snapshot()?.timeZone ?? browserTimeZone()) ?? 'Unknown';
  }
  windowBurn(window: { usedPct: number | null; resetAt: string | null }, durationHours: number | null | undefined): number | null {
    if (window.usedPct == null || !window.resetAt || durationHours == null) return null;
    const remaining = Math.max(0, Date.parse(window.resetAt) - Date.now());
    const elapsed = durationHours * 3_600_000 - remaining;
    return elapsed > 0 ? window.usedPct / (elapsed / 3_600_000) : null;
  }
  projected(window: { usedPct: number | null; resetAt: string | null }, durationHours: number | null | undefined): number | null {
    const burn = this.windowBurn(window, durationHours);
    return burn == null || durationHours == null ? null : Math.min(999, burn * durationHours);
  }
  formatTokens(value: number): string {
    if (value < 1_000) return value.toLocaleString('en-US');
    if (value < 1_000_000) return `${(value / 1_000).toFixed(value < 10_000 ? 1 : 0)}K`;
    return `${(value / 1_000_000).toFixed(2)}M`;
  }
  formatPct(value: number | null): string { return value == null ? 'Unknown' : `${value.toFixed(1)}% / h`; }

  private windowKey(window: QuotaWindow): string {
    return `${window.label}|${window.resetAt ?? ''}`;
  }
}
