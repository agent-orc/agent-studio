import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { AppTooltipDirective } from '../../../../components/tooltip/app-tooltip.directive';
import { formatCompactTokens } from '../../../tokens/token-number-format.util';
import type { OutcomeGroup, ScatterPoint } from '../../token-analysis.util';
import { OUTCOME_GROUP_LABELS, outcomeGroupColor } from '../../token-analysis.util';

const GROUPS: OutcomeGroup[] = ['done', 'stuck', 'open'];

/**
 * Tokens against rounds per card, coloured and shaped by outcome group
 * (AGT-3267). Three groups keep every pair distinguishable; the exact
 * outcome is in each point's tooltip.
 */
@Component({
  selector: 'app-token-analysis-scatter',
  standalone: true,
  imports: [AppTooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './token-analysis-scatter.html',
  styleUrl: './token-analysis-scatter.scss',
})
export class TokenAnalysisScatterComponent {
  readonly points = input.required<ScatterPoint[]>();
  readonly selectedId = input<string | null>(null);
  readonly cardSelect = output<string>();

  readonly fmt = formatCompactTokens;
  readonly legend = GROUPS.map(group => ({ group, label: OUTCOME_GROUP_LABELS[group], color: outcomeGroupColor(group) }));
  readonly maxRounds = computed(() => Math.max(1, ...this.points().map(p => p.rounds)));
  readonly maxTokens = computed(() => Math.max(1, ...this.points().map(p => p.tokens)));
  readonly roundTicks = computed(() => {
    const max = this.maxRounds();
    return max <= 6 ? Array.from({ length: max }, (_, i) => i + 1) : [1, Math.round(max / 2), max];
  });

  readonly placed = computed(() => {
    const maxRounds = this.maxRounds();
    const maxTokens = this.maxTokens();
    // Deterministic jitter spreads cards that share a round count.
    return this.points().map((point, i) => ({
      ...point,
      color: outcomeGroupColor(point.group),
      left: maxRounds <= 1 ? 50 : ((point.rounds - 1) / (maxRounds - 1)) * 92 + 4 + ((i % 5) - 2) * 0.6,
      bottom: (point.tokens / maxTokens) * 92 + 2,
      tip: `${point.label} · ${point.title}\n${formatCompactTokens(point.tokens)} tokens · ${point.rounds} round${point.rounds === 1 ? '' : 's'} · ${point.outcome}`,
    }));
  });

  tickLeft(round: number): number {
    const max = this.maxRounds();
    return max <= 1 ? 50 : ((round - 1) / (max - 1)) * 92 + 4;
  }
}
