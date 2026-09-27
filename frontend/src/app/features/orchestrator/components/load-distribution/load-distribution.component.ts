import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { CliUsageStore } from '../../../tokens';
import type { CliUsageQuotaRow, TokenSummaryByModel } from '../../../tokens';
import type { OrchestratorLogEntry } from '../../models/orchestrator.model';
import { AppTooltipDirective } from '../../../../components/tooltip/app-tooltip.directive';

type Period = '1h' | '24h' | '7d';
interface UsageEvent { ts: number; tokens: number; }
interface ModelRow {
  model: string;
  tokens: number;
  calls: number;
  cost: number | null;
  effort: { level: string; tokens: number; pct: number }[];
  points: string;
}

@Component({
  selector: 'app-load-distribution',
  standalone: true,
  imports: [AppTooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './load-distribution.component.html',
  styleUrl: './load-distribution.component.scss',
})
export class LoadDistributionComponent implements OnInit, OnDestroy {
  readonly entries = input<OrchestratorLogEntry[]>([]);
  readonly openTaskRequest = output<{ jobId: string; watchPath: string }>();
  readonly store = inject(CliUsageStore);
  readonly period = signal<Period>('24h');
  readonly decisionFilter = signal('all');
  readonly periods: { id: Period; label: string; hours: number }[] = [
    { id: '1h', label: 'Last hour', hours: 1 },
    { id: '24h', label: '24 hours', hours: 24 },
    { id: '7d', label: '7 days', hours: 168 },
  ];
  readonly decisionKinds = ['all', 'switch', 'downshift', 'throttle', 'wait'];
  readonly periodHours = computed(() => this.periods.find(p => p.id === this.period())?.hours ?? 24);
  readonly windowStart = computed(() => Date.now() - this.periodHours() * 3_600_000);
  readonly periodEntries = computed(() => this.entries().filter(e => Date.parse(e.ts) >= this.windowStart()));
  readonly decisions = computed(() => this.periodEntries()
    .filter(e => this.decisionAction(e) !== null)
    .filter(e => this.decisionFilter() === 'all' || this.decisionAction(e) === this.decisionFilter())
    .sort((a, b) => Date.parse(b.ts) - Date.parse(a.ts)));
  readonly completedCards = computed(() => this.periodEntries()
    .filter(e => /complete|done|finish/i.test(`${e.topic} ${e.summary}`)).length);
  readonly totalPeriodTokens = computed(() => this.modelRows().reduce((sum, row) => sum + row.tokens, 0));

  readonly modelRows = computed<ModelRow[]>(() => {
    const grouped = new Map<string, { tokens: number; calls: number; levels: Map<string, number>; events: UsageEvent[] }>();
    for (const entry of this.periodEntries()) {
      const usage = entry.tokenUsage;
      if (!usage) continue;
      const model = usage.model?.trim() || 'Unattributed model';
      const tokens = usage.inputTokens + usage.outputTokens + usage.cacheReadTokens + usage.cacheCreationTokens;
      const row = grouped.get(model) ?? { tokens: 0, calls: 0, levels: new Map<string, number>(), events: [] as UsageEvent[] };
      const level = usage.thinkingLevel?.trim() || 'unattributed';
      row.tokens += tokens;
      row.calls += 1;
      row.levels.set(level, (row.levels.get(level) ?? 0) + tokens);
      row.events.push({ ts: Date.parse(entry.ts), tokens });
      grouped.set(model, row);
    }
    return [...grouped.entries()].map(([model, value]) => ({
      model,
      tokens: value.tokens,
      calls: value.calls,
      cost: this.estimatePeriodCost(model, value.tokens),
      effort: [...value.levels.entries()].map(([level, tokens]) => ({ level, tokens, pct: value.tokens ? tokens / value.tokens * 100 : 0 })),
      points: this.linePoints(value.events),
    })).sort((a, b) => b.tokens - a.tokens);
  });

  readonly trendTotal = computed(() => {
    const source = this.period() === '7d' ? this.store.timeline7d() : this.store.timeline24h();
    return source?.cells.filter(c => Date.parse(c.bucketStart) >= this.windowStart())
      .reduce((sum, c) => sum + c.total, 0) ?? 0;
  });

  ngOnInit(): void { this.store.ensureQuotaStarted(); this.store.startDetail(); }
  ngOnDestroy(): void { this.store.stopDetail(); }
  selectPeriod(period: Period): void { this.period.set(period); }

  projection(row: CliUsageQuotaRow, label: '5h' | '7d'): number | null {
    const window = row.windows.find(w => w.label.toLowerCase().includes(label));
    if (window?.usedPct == null || !window.resetAt) return null;
    const duration = label === '5h' ? 5 * 3_600_000 : 7 * 24 * 3_600_000;
    const elapsed = duration - Math.max(0, Date.parse(window.resetAt) - Date.now());
    return elapsed <= 0 ? window.usedPct : Math.min(999, window.usedPct * duration / elapsed);
  }
  projectionLabel(row: CliUsageQuotaRow, label: '5h' | '7d'): string {
    const projected = this.projection(row, label);
    if (projected === null) return 'Projection unavailable';
    const state = projected > 100 ? 'Projected to exceed' : 'Within window';
    return `${state} · ${projected.toFixed(0)}% at reset`;
  }
  windowPct(row: CliUsageQuotaRow, label: '5h' | '7d'): number | null {
    return row.windows.find(w => w.label.toLowerCase().includes(label))?.usedPct ?? null;
  }
  resetLabel(row: CliUsageQuotaRow, label: '5h' | '7d'): string {
    return row.windows.find(w => w.label.toLowerCase().includes(label))?.resetLabel ?? 'reset unknown';
  }
  decisionAction(entry: OrchestratorLogEntry): string | null {
    const value = `${entry.topic} ${entry.summary}`.toLowerCase();
    return this.decisionKinds.slice(1).find(kind => value.includes(kind)) ?? null;
  }
  openTask(entry: OrchestratorLogEntry): void {
    if (!entry.jobId || !entry.watchPath) return;
    this.openTaskRequest.emit({ jobId: entry.jobId, watchPath: entry.watchPath });
  }
  formatTokens(value: number): string {
    if (value < 1_000) return value.toLocaleString('en-US');
    if (value < 1_000_000) return `${(value / 1_000).toFixed(value < 10_000 ? 1 : 0)}K`;
    return `${(value / 1_000_000).toFixed(2)}M`;
  }
  formatTime(value: string): string { return new Date(value).toLocaleString('en-US'); }

  private estimatePeriodCost(model: string, tokens: number): number | null {
    const aggregate = this.store.tokens()?.byModel.find(row => row.model === model);
    if (!aggregate?.modelPriced) return null;
    const aggregateTokens = this.aggregateTokens(aggregate);
    return aggregateTokens > 0 ? tokens * aggregate.estimatedApiCostUsd / aggregateTokens : null;
  }
  private aggregateTokens(value: TokenSummaryByModel): number {
    return value.inputTokens + value.outputTokens + value.cacheReadTokens + value.cacheCreationTokens;
  }
  private linePoints(events: UsageEvent[]): string {
    const sorted = [...events].sort((a, b) => a.ts - b.ts);
    const span = this.periodHours() * 3_600_000;
    const total = Math.max(1, sorted.reduce((sum, event) => sum + event.tokens, 0));
    let cumulative = 0;
    const points = ['0,38'];
    for (const event of sorted) {
      cumulative += event.tokens;
      const x = Math.max(0, Math.min(100, (event.ts - this.windowStart()) / span * 100));
      points.push(`${x.toFixed(1)},${(38 - cumulative / total * 34).toFixed(1)}`);
    }
    return points.join(' ');
  }
}
