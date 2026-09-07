import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import type { CliType } from '../../../../models/task.model';
import type { QuotaWindow } from '../../../../features/quota';
import { DialogComponent } from '../../../../components/dialog/dialog.component';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { AppTooltipDirective } from '../../../../components/tooltip/app-tooltip.directive';
import type { CliUsageQuotaRow } from '../../services/cli-usage.store';
import type { AdHocUsageAggregate, TokenSummaryAggregate } from '../../models/tokens.model';
import {
  formatTokenCostTotal,
  formatTokenCount,
  formatTokenCurrencyUsd,
} from '../../token-number-format.util';
import {
  type ModelUsageRow,
  groupModelUsageRows,
  modelUsageTokenTotal,
  normalizeModelGroupThreshold,
  readModelGroupThreshold,
  readOtherModelsExpanded,
  sumModelUsageRows,
  writeModelGroupThreshold,
  writeOtherModelsExpanded,
} from './cli-usage-model-rows.util';

type WindowTone = 'ok' | 'warn' | 'hot' | 'unknown';

/**
 * Presentational projection of a reported quota window: the raw
 * {@link QuotaWindow} plus the derived percentage, traffic-light tone,
 * clamped bar width, and the reset / limit strings the card renders.
 * No mapping or refresh logic lives here - it only reshapes the input
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

/**
 * One detail modal for a single CLI's usage. Opened by clicking that
 * CLI's card in the status-bar quota strip - one modal per CLI type, no
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
  readonly cliType = input.required<CliType>();
  readonly row = input<CliUsageQuotaRow | null>(null);
  readonly tokens = input<TokenSummaryAggregate | null>(null);
  readonly adhoc = input<AdHocUsageAggregate | null>(null);
  readonly refreshing = input(false);

  readonly groupingThresholdPct = signal(readModelGroupThreshold());
  readonly otherModelsExpanded = signal(readOtherModelsExpanded());

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

  /** Date range of the recorded telemetry, derived from data - not config.
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

  /** Grand totals always include every row, independent of grouping state. */
  readonly totals = computed(() => sumModelUsageRows(this.modelRows()));

  readonly groupedModelRows = computed(() =>
    groupModelUsageRows(this.modelRows(), this.groupingThresholdPct()));

  setGroupingThreshold(value: unknown): void {
    const threshold = normalizeModelGroupThreshold(value);
    this.groupingThresholdPct.set(threshold);
    writeModelGroupThreshold(threshold);
  }

  toggleOtherModels(): void {
    const expanded = !this.otherModelsExpanded();
    this.otherModelsExpanded.set(expanded);
    writeOtherModelsExpanded(expanded);
  }

  otherModelsLabel(): string {
    const count = this.groupedModelRows().otherTotals.models;
    return `Other (${count} model${count === 1 ? '' : 's'})`;
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
    if (priced) return this.formatUsd(value);
    return value === 0 ? 'Unpriced' : this.formatCostTotal(value, 1);
  }

  totalTokens(row: ModelUsageRow): number {
    return modelUsageTokenTotal(row);
  }

  /** Read + creation cache tokens folded into one "Cache" column value. */
  cacheTokens(row: ModelUsageRow): number {
    return row.cacheReadTokens + row.cacheCreationTokens;
  }

  private toneForPct(pct: number | null): WindowTone {
    if (pct === null) return 'unknown';
    if (pct < 70) return 'ok';
    if (pct < 90) return 'warn';
    return 'hot';
  }

  formatTokens(n: number): string {
    return formatTokenCount(n, { maximumUnit: 'M' });
  }

  formatUsd(n: number): string {
    return formatTokenCurrencyUsd(n);
  }

  formatCostTotal(costUsd: number, unpricedModels: number): string {
    return formatTokenCostTotal(costUsd, unpricedModels);
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
