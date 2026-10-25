import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { AppTooltipDirective } from '../../../../components/tooltip/app-tooltip.directive';
import { formatCompactTokens } from '../../../tokens/token-number-format.util';
import type { TimeChart } from '../../token-analysis.util';

interface Column {
  day: string;
  total: number;
  incomplete: boolean;
  segments: { key: string; color: string; value: number }[];
  tip: string;
}

/**
 * Daily tokens stacked by model and reasoning level (AGT-3267). Days that
 * are still open or hold in-flight runs are marked incomplete, so a short
 * last column never reads as a drop.
 */
@Component({
  selector: 'app-token-analysis-time-chart',
  standalone: true,
  imports: [AppTooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './token-analysis-time-chart.html',
  styleUrl: './token-analysis-time-chart.scss',
})
export class TokenAnalysisTimeChartComponent {
  readonly chart = input.required<TimeChart>();

  readonly fmt = formatCompactTokens;
  readonly max = computed(() => Math.max(0, ...this.chart().totals));
  readonly hasIncomplete = computed(() => this.chart().incomplete.some(Boolean));

  readonly columns = computed<Column[]>(() => {
    const chart = this.chart();
    return chart.days.map((day, i) => {
      const segments = chart.series
        .map(series => ({ key: series.key, color: series.color, value: series.values[i] }))
        .filter(segment => segment.value > 0);
      const lines = segments.map(segment => `${segment.key}: ${formatCompactTokens(segment.value)}`);
      const flag = chart.incomplete[i] ? ' (incomplete)' : '';
      return {
        day, total: chart.totals[i], incomplete: chart.incomplete[i], segments,
        tip: [`${day}${flag}: ${formatCompactTokens(chart.totals[i])} tokens`, ...lines].join('\n'),
      };
    });
  });

  readonly ticks = computed(() => {
    const days = this.chart().days;
    if (days.length <= 2) return days;
    return [days[0], days[Math.floor(days.length / 2)], days[days.length - 1]];
  });

  height(value: number): string {
    const max = this.max();
    return max > 0 ? `${(value / max) * 100}%` : '0%';
  }
}
