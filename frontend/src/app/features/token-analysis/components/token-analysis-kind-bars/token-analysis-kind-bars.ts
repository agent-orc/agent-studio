import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { AppTooltipDirective } from '../../../../components/tooltip/app-tooltip.directive';
import { formatCompactTokens } from '../../../tokens/token-number-format.util';
import type { StackRow } from '../../token-analysis.util';
import { kindColor, kindLabel } from '../../token-analysis.util';

/** Stacked usage kinds per card for the most expensive cards (AGT-3267). */
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
  readonly selectedId = input<string | null>(null);
  readonly cardSelect = output<string>();

  readonly max = computed(() => this.rows().reduce((m, row) => Math.max(m, row.total), 0));
  readonly legend = computed(() => this.kinds().map(kind => ({ kind, label: kindLabel(kind), color: kindColor(kind) })));

  readonly fmt = formatCompactTokens;

  width(value: number): string {
    const max = this.max();
    return max > 0 ? `${(value / max) * 100}%` : '0%';
  }

  segmentTip(row: StackRow, label: string, value: number): string {
    const share = row.total > 0 ? Math.round((value / row.total) * 100) : 0;
    return `${row.label} · ${label}: ${formatCompactTokens(value)} tokens (${share}%)`;
  }
}
