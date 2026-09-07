import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import type { CliType } from '../../../../models/task.model';
import type { QuotaWindow } from '../../../../features/quota';
import { DialogComponent } from '../../../../components/dialog/dialog.component';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { AppTooltipDirective } from '../../../../components/tooltip/app-tooltip.directive';
import type { CliUsageQuotaRow } from '../../services/cli-usage.store';
import type { AdHocUsageAggregate, TokenSummaryAggregate } from '../../models/tokens.model';
import { CostBreakdownService } from '../../services/cost-breakdown.service';
import { formatUsageCurrency, formatUsageTokens } from '../../usage-number-format.util';
import {
  clampThreshold,
  normalizeModelUsageRows,
  projectModelUsageRows,
  readModelUsagePreferences,
  writeModelUsagePreferences,
  type ModelUsageRow,
  type OtherModelUsageRow,
  type RawModelUsageRow,
} from '../../model-usage-table.util';

type TableModelUsageRow = ModelUsageRow | OtherModelUsageRow;

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
  unpricedModels: number;
}

/**
 * One detail modal for a single CLI's usage. Opened by clicking that
 * CLI's card in the status-bar quota strip — one modal per CLI type, no
 * shared hover tooltip and no grouped multi-CLI view. Shows every quota
 * window the probe reported (so Claude / Codex surface both their 5h and
 * weekly windows), the plan / freshness header, this CLI's recorded models,
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
  private readonly initialPreferences = readModelUsagePreferences();
  readonly cliType = input.required<CliType>();
  readonly row = input<CliUsageQuotaRow | null>(null);
  readonly tokens = input<TokenSummaryAggregate | null>(null);
  readonly adhoc = input<AdHocUsageAggregate | null>(null);
  readonly refreshing = input(false);
  readonly groupingThresholdPercent = signal(this.initialPreferences.thresholdPercent);
  readonly otherExpanded = signal(this.initialPreferences.expanded);

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

  readonly modelRows = computed<ModelUsageRow[]>(() => {
    const cli = this.cliType();
    const rows: RawModelUsageRow[] = [];
    for (const m of this.tokens()?.byModel ?? []) {
      if (!this.modelBelongsToCli(m.modelId ?? m.model, cli)) continue;
      rows.push({ ...m, source: 'project runtime', cacheIncludedInInput: cli === 'codex' });
    }
    for (const m of this.adhoc()?.byModel ?? []) {
      if (!this.modelBelongsToCli(m.modelId ?? m.model, cli)) continue;
      rows.push({ ...m, source: 'ad-hoc', cacheIncludedInInput: cli === 'codex' });
    }
    return normalizeModelUsageRows(rows);
  });

  readonly modelProjection = computed(() =>
    projectModelUsageRows(this.modelRows(), this.groupingThresholdPercent()));

  /** Totals always use the complete normalized source rows, never the collapse. */
  readonly totals = computed<UsageTotals>(() => {
    const projection = this.modelProjection();
    return {
      costUsd: projection.estimatedApiCostUsd,
      tokens: projection.totalTokens,
      models: projection.modelCount,
      unpricedModels: projection.unpricedModelCount,
    };
  });

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

  readonly formatTokens = formatUsageTokens;
  readonly formatUsd = formatUsageCurrency;

  totalCostLabel(): string {
    return this.costWithUnpricedMarker(this.totals().costUsd, this.totals().unpricedModels);
  }

  rowCostLabel(row: TableModelUsageRow): string {
    if (row.kind === 'other') {
      return this.costWithUnpricedMarker(row.estimatedApiCostUsd, row.unpricedModelCount);
    }
    if (row.modelPriced) return this.formatUsd(row.estimatedApiCostUsd);
    return row.estimatedApiCostUsd > 0
      ? this.costWithUnpricedMarker(row.estimatedApiCostUsd, 1)
      : 'Unknown';
  }

  totalTokens(row: ModelUsageRow): number {
    return row.totalTokens;
  }
  /** Read + creation cache tokens folded into one "Cache" column value. */
  cacheTokens(row: TableModelUsageRow): number {
    return row.cacheReadTokens + row.cacheCreationTokens;
  }
  cacheIncludedInInput(row: TableModelUsageRow): boolean {
    return row.kind === 'other' ? this.cliType() === 'codex' : row.cacheIncludedInInput;
  }

  setGroupingThreshold(event: Event): void {
    const input = event.target as HTMLInputElement;
    const threshold = clampThreshold(input.valueAsNumber);
    input.value = String(threshold);
    this.groupingThresholdPercent.set(threshold);
    this.persistModelUsagePreferences();
  }

  toggleOther(): void {
    this.otherExpanded.update(expanded => !expanded);
    this.persistModelUsagePreferences();
  }

  showTotalCalculation(): void {
    this.costBreakdown.show(this.modelRows().map(row => this.priceItem(row)),
      `${this.title()} recorded usage cost`);
  }

  showModelCalculation(row: ModelUsageRow): void {
    this.costBreakdown.show([this.priceItem(row)], `${row.model} cost calculation`);
  }

  showOtherCalculation(row: OtherModelUsageRow): void {
    this.costBreakdown.show(row.rows.map(item => this.priceItem(item)), 'Other models cost calculation');
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

  private costWithUnpricedMarker(costUsd: number, unpricedModels: number): string {
    const subtotal = this.formatUsd(costUsd);
    return unpricedModels > 0 ? `${subtotal} + ${unpricedModels} unpriced` : subtotal;
  }

  private persistModelUsagePreferences(): void {
    writeModelUsagePreferences({
      thresholdPercent: this.groupingThresholdPercent(),
      expanded: this.otherExpanded(),
    });
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
