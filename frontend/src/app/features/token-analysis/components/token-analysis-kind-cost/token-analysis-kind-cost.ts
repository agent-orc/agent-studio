import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { AppTooltipDirective } from '@studio/ui/components/tooltip/app-tooltip.directive';
import { formatCompactTokens, formatCompactUsd } from '../../../tokens/token-number-format.util';
import type { KindCostBreakdown } from '../../token-analysis.util';

/**
 * Theoretical cost per usage kind (coding run, fix round, review, gate,
 * enrichment and one-shot calls, ...) for the selected period and filters
 * (AGT-3805). Workspace scope adds one cost column per project; project
 * scope shows the one project. Rows sum to the card table's cost (R3).
 */
@Component({
  selector: 'app-token-analysis-kind-cost',
  standalone: true,
  imports: [AppTooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './token-analysis-kind-cost.html',
  styleUrl: './token-analysis-kind-cost.scss',
})
export class TokenAnalysisKindCostComponent {
  readonly breakdown = input.required<KindCostBreakdown>();
  readonly days = input.required<number>();

  readonly fmt = formatCompactTokens;
  readonly usd = formatCompactUsd;

  /** Per-project columns only when more than one project is visible. */
  readonly projects = computed(() => this.breakdown().projects.length > 1 ? this.breakdown().projects : []);
  readonly period = computed(() => this.days() === 1 ? 'Last day' : `Last ${this.days()} days`);

  percent(share: number): string {
    return `${Math.round(share * 100)}%`;
  }
}
