import { ChangeDetectionStrategy, Component, computed, inject, input, output } from '@angular/core';
import type { CliType } from '../../../../models/task.model';
import type { QuotaWindow } from '../../../../features/quota';
import { DialogComponent } from '../../../../components/dialog/dialog.component';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { AppTooltipDirective } from '../../../../components/tooltip/app-tooltip.directive';
import type { CliUsageQuotaRow } from '../../services/cli-usage.store';
import type { AdHocUsageAggregate, TokenSummaryAggregate } from '../../models/tokens.model';
import { CostBreakdownService } from '../../services/cost-breakdown.service';
import { formatCompactTokens, formatCompactUsd } from '../../token-number-format.util';
import { RecordedModelUsageGroupingStore } from '../../services/recorded-model-usage-grouping.store';

interface ModelUsageRow {
  model: string;
  source: string;
  /** OpenAI reports cached input as a subset of input, while Anthropic
   *  reports cache-read tokens as a separate category. */
  cacheIncludedInInput: boolean;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;
  estimatedApiCostUsd: number;
  modelPriced: boolean;
  /** True for the synthetic "Other (n models)" row that folds every
   *  below-threshold model together. */
  isOtherSummary?: boolean;
  /** Number of models folded into the "Other" row. */
  otherCount?: number;
  /** True for a real model row shown because the "Other" group is expanded. */
  isOtherChild?: boolean;
}

type WindowTone = 'ok' | 'warn' | 'hot' | 'unknown';

/**
 * Presentational projection of a reported quota window: the raw
 * {@link QuotaWindow} plus the derived percentage, traffic-light tone,
 * clamped bar width, and the reset / limit strings the card renders.
 * No mapping or refresh logic lives here — it only reshapes the input
 * row into what the template draws.
 */
interface WindowView {
  label: string;
  pct: number | null;
  pctLabel: string;
  remainingLabel: string | null;
  barPct: number;
  tone: WindowTone;
  /** Implied cap, or null when the window reports no usable limit. */
  limit: string | null;
  reset: string | null;
}

/** Summary head over the model table: totals shown as stat tiles. */
interface UsageTotals {
  costUsd: number;
  tokens: number;
  models: number;
  /** Distinct models (across every row, grouped or not) with no resolved price. */
  unpricedModels: number;
  anyPriced: boolean;
  allPriced: boolean;
}

/**
 * One detail modal for a single CLI's usage. Opened by clicking that
 * CLI's card in the status-bar quota strip — one modal per CLI type, no
 * shared hover tooltip and no grouped multi-CLI view. Shows every quota
 * window the probe reported (so Claude / Codex surface both their 5h and
 * weekly windows), the plan / freshness header, this CLI's top models,
 * and any probe error. The footer drops into the full CLI-Management
 * caps surface or re-probes just this CLI.
 *
 * Purely presentational: the host (`<app-usage-hover-panel>`) owns the
 * `CliUsageStore` polling, the ModalStack registration, and the
 * open-state; this component renders the inputs and bubbles intent.
 */
@Component({
  selector: 'app-cli-usage-modal',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DialogComponent, TooltipDirective, AppTooltipDirective],
  templateUrl: './cli-usage-modal.html',
  styleUrl: './cli-usage-modal.scss',
})
export class CliUsageModalComponent {
  private readonly costBreakdown = inject(CostBreakdownService);
  private readonly grouping = inject(RecordedModelUsageGroupingStore);
  readonly cliType = input.required<CliType>();
  readonly row = input<CliUsageQuotaRow | null>(null);
  readonly tokens = input<TokenSummaryAggregate | null>(null);
  readonly adhoc = input<AdHocUsageAggregate | null>(null);
  readonly refreshing = input(false);

  readonly closeRequest = output<void>();
  readonly refresh = output<void>();
  readonly manageCaps = output<void>();

  readonly title = computed(() => this.row()?.label ?? this.cliLabel(this.cliType()));

  readonly failureLabel = computed(() => {
    const row = this.row();
    return row?.showingLastGood ? row.probeFailureLabel ?? null : null;
  });

  readonly subtitle = computed(() => {
    const r = this.row();
    if (!r) return 'No data yet';
    const parts: string[] = [r.plan ?? 'No plan reported'];
    if (r.source) parts.push(r.source);
    parts.push(r.freshness);
    return parts.join(' · ');
  });

  readonly windows = computed<QuotaWindow[]>(() => this.row()?.windows ?? []);

  /** Reshapes each reported window into its card projection (pct, tone,
   *  bar width, reset countdown). Pure derivation of the input row. */
  readonly windowViews = computed<WindowView[]>(() =>
    this.windows().map((w) => {
      const pct = w.usedPct === null ? null : Math.round(w.usedPct);
      const barPct = pct === null ? 0 : Math.max(0, Math.min(100, pct));
      const limit = this.limitText(w);
      return {
        label: w.label,
        pct,
        pctLabel: pct === null ? 'Unknown' : `${pct}% used`,
        remainingLabel: pct === null ? null : `${Math.max(0, 100 - pct)}% left`,
        barPct,
        tone: this.toneForPct(pct),
        limit: limit === 'n/a' ? null : limit,
        reset: w.resetLabel ?? null,
      };
    }),
  );

  /** Summed cost / token totals across every recorded model row, grouped
   *  or not — the "Summen-Kopf" over the model table. The grouped "Other"
   *  row folds display, but every underlying model still counts toward
   *  the total (style-guide R3: aggregate = sum of visible children).
   *  Presentational only. */
  readonly totals = computed<UsageTotals>(() => {
    const rows = this.allModelRows();
    let costUsd = 0;
    let tokens = 0;
    let unpricedModels = 0;
    for (const r of rows) {
      tokens += this.totalTokens(r);
      if (r.modelPriced) costUsd += r.estimatedApiCostUsd;
      else unpricedModels++;
    }
    return {
      costUsd,
      tokens,
      models: rows.length,
      unpricedModels,
      anyPriced: rows.length > 0 && unpricedModels < rows.length,
      allPriced: rows.length > 0 && unpricedModels === 0,
    };
  });

  /** Total cost label used by both the header tile and the table footer:
   *  the sum over every priced row, with a "+n unpriced" marker instead
   *  of ever rendering "Unknown" for the whole total. */
  totalCostLabel(): string {
    const t = this.totals();
    if (t.models === 0) return 'n/a';
    const marker = t.unpricedModels > 0 ? ` + ${t.unpricedModels} unpriced` : '';
    return this.formatUsd(t.costUsd) + marker;
  }

  /** Date range of the recorded telemetry, derived from data — not config.
   *  Returns a compact "since <date> · as of <date>" string, or null when
   *  neither tokens nor adhoc carry any activity timestamps. */
  readonly telemetryRange = computed<string | null>(() => {
    const tokens = this.tokens();
    const adhoc = this.adhoc();

    let firstIso: string | null = tokens?.firstActivity ?? null;
    let lastIso: string | null = tokens?.lastActivity ?? null;

    if (adhoc?.byDay?.length) {
      const sorted = [...adhoc.byDay].sort((a, b) => a.date.localeCompare(b.date));
      const adhocFirst = sorted[0].date;
      const adhocLast = sorted[sorted.length - 1].date;
      if (!firstIso || adhocFirst < firstIso) firstIso = adhocFirst;
      if (!lastIso || adhocLast > lastIso.slice(0, 10)) lastIso = adhoc.logModifiedAt ?? adhocLast;
    }

    const firstLabel = firstIso ? this.formatDateLabel(firstIso) : null;
    const lastLabel = lastIso ? this.formatDateLabel(lastIso) : null;
    if (!firstLabel && !lastLabel) return null;
    if (firstLabel && lastLabel && firstLabel !== lastLabel) return `since ${firstLabel} · as of ${lastLabel}`;
    if (lastLabel) return `as of ${lastLabel}`;
    return null;
  });

  private formatDateLabel(iso: string): string {
    try {
      const d = new Date(iso);
      if (isNaN(d.getTime())) return iso.slice(0, 10);
      return d.toISOString().slice(0, 10);
    } catch {
      return iso.slice(0, 10);
    }
  }

  /** Every recorded model row for this CLI, busiest first. Not capped —
   *  callers that need a compact view use {@link modelRows} instead. */
  private readonly allModelRows = computed<ModelUsageRow[]>(() => {
    const cli = this.cliType();
    const rows: ModelUsageRow[] = [];
    for (const m of this.tokens()?.byModel ?? []) {
      if (!this.modelBelongsToCli(m.model, cli)) continue;
      const row = { ...m, source: 'project runtime', cacheIncludedInInput: cli === 'codex' };
      if (this.totalTokens(row) > 0) rows.push(row);
    }
    for (const m of this.adhoc()?.byModel ?? []) {
      if (!this.modelBelongsToCli(m.model, cli)) continue;
      const row = { ...m, source: 'ad-hoc', cacheIncludedInInput: cli === 'codex' };
      if (this.totalTokens(row) > 0) rows.push(row);
    }
    return rows.sort((a, b) => this.totalTokens(b) - this.totalTokens(a));
  });

  /** Whether the "Other (n models)" group is left expanded, remembered per viewer. */
  readonly otherExpanded = computed(() => this.grouping.expanded(this.cliType()));

  /**
   * Table rows: models at or above the token-share threshold render
   * individually; the rest fold into one "Other (n models)" row so a long
   * tail of rarely used models does not crowd out the ones that matter.
   * The group still counts fully toward {@link totals} and expands in
   * place to show its members.
   */
  readonly modelRows = computed<ModelUsageRow[]>(() => {
    const rows = this.allModelRows();
    if (rows.length === 0) return rows;

    const totalTokens = rows.reduce((sum, r) => sum + this.totalTokens(r), 0);
    const threshold = this.grouping.threshold(this.cliType());
    const major: ModelUsageRow[] = [];
    const minor: ModelUsageRow[] = [];
    for (const row of rows) {
      const share = totalTokens > 0 ? this.totalTokens(row) / totalTokens : 0;
      (share >= threshold ? major : minor).push(row);
    }
    if (minor.length === 0) return major;

    const summary = this.otherSummaryRow(minor);
    if (!this.otherExpanded()) return [...major, summary];
    return [...major, summary, ...minor.map(row => ({ ...row, isOtherChild: true }))];
  });

  private otherSummaryRow(minor: ModelUsageRow[]): ModelUsageRow {
    return {
      model: `Other (${minor.length} model${minor.length === 1 ? '' : 's'})`,
      source: '',
      cacheIncludedInInput: minor[0]?.cacheIncludedInInput ?? false,
      inputTokens: minor.reduce((s, r) => s + r.inputTokens, 0),
      outputTokens: minor.reduce((s, r) => s + r.outputTokens, 0),
      cacheReadTokens: minor.reduce((s, r) => s + r.cacheReadTokens, 0),
      cacheCreationTokens: minor.reduce((s, r) => s + r.cacheCreationTokens, 0),
      estimatedApiCostUsd: minor.reduce((s, r) => s + (r.modelPriced ? r.estimatedApiCostUsd : 0), 0),
      modelPriced: minor.every(r => r.modelPriced),
      isOtherSummary: true,
      otherCount: minor.length,
    };
  }

  toggleOther(): void {
    this.grouping.toggleExpanded(this.cliType());
  }

  limitText(window: QuotaWindow): string {
    if (window.used !== null && window.limit !== null) {
      return `${window.used} / ${window.limit}${window.unit ? ' ' + window.unit : ''}`;
    }
    // Operator rule: a "%" window with no explicit numeric limit is capped
    // at 100% (the CLI reports "66% used", so the limit is implicitly 100%).
    // Show the implied cap instead of a bare "n/a" so Codex windows
    // (used/limit null, only usedPct) read as "66%" against "100%".
    if (window.unit === '%' && window.usedPct !== null) {
      return '100%';
    }
    return 'n/a';
  }

  costLabel(value: number, priced: boolean): string {
    return priced ? this.formatUsd(value) : 'Unknown';
  }

  totalTokens(row: ModelUsageRow): number {
    return row.inputTokens
      + row.outputTokens
      + row.cacheCreationTokens
      + (row.cacheIncludedInInput ? 0 : row.cacheReadTokens);
  }

  /** Read + creation cache tokens folded into one "Cache" column value. */
  cacheTokens(row: ModelUsageRow): number {
    return row.cacheReadTokens + row.cacheCreationTokens;
  }

  showTotalCalculation(): void {
    // Every underlying model, not the display-grouped rows: the synthetic
    // "Other" row has no real model id the pricing dialog could look up.
    this.costBreakdown.show(this.allModelRows().map(row => this.priceItem(row)),
      `${this.title()} recorded usage cost`);
  }

  showModelCalculation(row: ModelUsageRow): void {
    if (row.isOtherSummary) {
      this.toggleOther();
      return;
    }
    this.costBreakdown.show([this.priceItem(row)], `${row.model} cost calculation`);
  }

  private priceItem(row: ModelUsageRow) {
    return {
      model: row.model,
      label: row.source,
      inputTokens: row.inputTokens,
      outputTokens: row.outputTokens,
      cacheReadTokens: row.cacheReadTokens,
      cacheWriteTokens: row.cacheCreationTokens,
    };
  }

  private toneForPct(pct: number | null): WindowTone {
    if (pct === null) return 'unknown';
    if (pct < 70) return 'ok';
    if (pct < 90) return 'warn';
    return 'hot';
  }

  formatTokens(n: number): string {
    return formatCompactTokens(n);
  }

  formatUsd(n: number): string {
    return formatCompactUsd(n);
  }

  private modelBelongsToCli(model: string, cliType: CliType): boolean {
    const m = (model ?? '').toLowerCase();
    switch (cliType) {
      case 'claude':
        return m.includes('claude') || m.includes('haiku') || m.includes('sonnet') || m.includes('opus');
      case 'codex':
        return m.includes('codex') || m.startsWith('gpt') || /^o\d/.test(m);
      case 'gemini':
        return m.includes('gemini');
      default:
        return false;
    }
  }

  private cliLabel(cli: CliType): string {
    switch (cli) {
      case 'claude': return 'Claude';
      case 'codex': return 'Codex';
      case 'gemini': return 'Gemini';
      default: return cli;
    }
  }
}
