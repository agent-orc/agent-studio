import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { AppTooltipDirective } from '@studio/ui/components/tooltip/app-tooltip.directive';
import { formatCompactTokens, formatCompactUsd } from '../../../tokens/token-number-format.util';
import type { AnalysisMeasure, StackRow } from '../../token-analysis.util';
import { kindColor, kindLabel } from '../../token-analysis.util';

/**
 * Stacked usage kinds per card for the top cards (AGT-3267). The rows are
 * ranked and sized by one measure, cost or tokens, and the header and every
 * value name it (AGT-3805).
 */
@Component({
  selector: 'app-token-analysis-kind-bars',
  standalone: true,
  imports: [AppTooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './token-analysis-kind-bars.html',
  styleUrl: './token-analysis-kind-bars.scss',
})
export class TokenAnalysisKindBarsComponent {
  readonly rows = input.required<StackRow[]>();
  readonly kinds = input.required<string[]>();
  readonly measure = input<AnalysisMeasure>('cost');
  readonly selectedId = input<string | null>(null);
  readonly cardSelect = output<string>();

  readonly max = computed(() => this.rows().reduce((m, row) => Math.max(m, row.total), 0));
  readonly legend = computed(() => this.kinds().map(kind => ({ kind, label: kindLabel(kind), color: kindColor(kind) })));

  readonly measureLabel = computed(() => this.measure() === 'cost' ? 'theoretical cost' : 'tokens');

  /** Bar end label; the header names the measure. */
  short(amount: number): string {
    return this.measure() === 'cost' ? formatCompactUsd(amount) : formatCompactTokens(amount);
  }

  value(amount: number): string {
    return this.measure() === 'cost' ? formatCompactUsd(amount) : `${formatCompactTokens(amount)} tokens`;
  }

  width(value: number): string {
    const max = this.max();
    return max > 0 ? `${(value / max) * 100}%` : '0%';
  }

  segmentTip(row: StackRow, label: string, value: number): string {
    const share = row.total > 0 ? Math.round((value / row.total) * 100) : 0;
    return `${row.label} · ${label}: ${this.value(value)} (${share}%)`;
  }
}
