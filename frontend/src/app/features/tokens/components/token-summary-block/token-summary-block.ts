import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, inject, input, signal } from '@angular/core';
import type { TokenSummary } from '../../../../features/tokens';
import { TaskService } from '../../../../services/task.service';
import { TokensApiService } from '../../../../features/tokens';
import { CostBreakdownService } from '../../services/cost-breakdown.service';
import type { TokenSummaryByModel } from '../../models/tokens.model';
import { formatUsageCurrency, formatUsageTokens } from '../../usage-number-format.util';

import { TooltipDirective } from 'coding-agent-chat/shared';
/**
 * Per-project token rollup block. Three rows:
 *
 * 1. **Amounts (real).** Total input / output / cache tokens across all
 *    orchestrator LLM calls on this project, with a per-model breakdown
 *    underneath. These numbers are accurate; they come straight from
 *    the orchestrator's CLI envelopes.
 * 2. **Theoretical API cost (estimate).** What the same tokens would
 *    have cost via Anthropic's API. Carried because it is a useful
 *    comparison and a sanity check, *not* because the user pays it.
 *    The disclaimer line is always shown.
 * 3. **Subscription quota link.** Pointer to the existing CLI Usage
 *    sheet (the 🪙 Usage button in the toolbar) which surfaces the
 *    real billing dimension - the user's Pro / Max plan windows.
 *
 * Mounted by `OrchestratorFeedComponent` (header) and
 * `ProjectDetailComponent` (group). 5s poll while mounted.
 */
@Component({
  selector: 'app-token-summary-block',
  standalone: true,
  imports: [TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './token-summary-block.html',
  styleUrl: './token-summary-block.scss'
})
export class TokenSummaryBlockComponent implements OnInit, OnDestroy {
  private readonly tokensApi = inject(TokensApiService);
  private readonly costBreakdown = inject(CostBreakdownService);
  readonly projectName = input.required<string>();

  private readonly jobService = inject(TaskService);
  readonly summary = signal<TokenSummary | null>(null);
  private pollTimer: ReturnType<typeof setInterval> | null = null;

  ngOnInit(): void {
    this.refresh();
    this.pollTimer = setInterval(() => this.refresh(), 5_000);
  }

  ngOnDestroy(): void {
    if (this.pollTimer != null) clearInterval(this.pollTimer);
    this.pollTimer = null;
  }

  refresh(): void {
    this.tokensApi.getTokenSummary(this.projectName()).subscribe({
      next: (s) => this.summary.set(s),
      error: () => { /* keep last value */ }
    });
  }

  readonly formatTokens = formatUsageTokens;
  readonly formatUsd = formatUsageCurrency;

  formatAggregateUsd(n: number, allModelsPriced: boolean): string {
    if (!allModelsPriced && n <= 0) return 'Unknown';
    return this.formatUsd(n);
  }

  showTotalCalculation(summary: TokenSummary): void {
    this.costBreakdown.show(summary.byModel.map(model => this.priceItem(model)),
      `${summary.project} cost calculation`);
  }

  showModelCalculation(model: TokenSummaryByModel): void {
    this.costBreakdown.show([this.priceItem(model)], `${model.modelId ?? model.model} cost calculation`);
  }

  private priceItem(model: TokenSummaryByModel) {
    return {
      model: model.modelId ?? model.model,
      label: `${model.calls} call${model.calls === 1 ? '' : 's'}`,
      inputTokens: model.inputTokens,
      outputTokens: model.outputTokens,
      cacheReadTokens: model.cacheReadTokens,
      cacheWriteTokens: model.cacheCreationTokens,
    };
  }
}
