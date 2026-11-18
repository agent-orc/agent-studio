import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { browserTimeZone, formatZonedWithUtc } from '../../../../services/usage-time.util';
import { formatCompactTokens, formatCompactUsd } from '../../../tokens/token-number-format.util';
import type { TokenAnalysisCardDetail } from '../../models/token-analysis.model';
import { StatusIndicatorComponent } from '../../../../components/status-indicator/status-indicator.component';
import { cardOutcomeTone, stepResultTone } from '../../token-analysis-status';
import { eventShares, formatDuration, kindColor, kindLabel, kindTotals } from '../../token-analysis.util';

/**
 * Drill-down of one card (AGT-3267): every coding run, fix round, review
 * aspect, grade, gate, decision and chat turn in time order with model,
 * reasoning level, tokens by type, duration and result, so one sees where
 * the card spent its tokens. Reached from the analysis table, the card
 * detail, and the board card's token popover.
 */
@Component({
  selector: 'app-card-token-drilldown',
  standalone: true,
  imports: [StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './card-token-drilldown.html',
  styleUrl: './card-token-drilldown.scss',
})
export class CardTokenDrilldownComponent {
  readonly detail = input<TokenAnalysisCardDetail | null>(null);
  readonly loading = input(false);
  readonly error = input<string | null>(null);
  readonly closed = output<void>();
  readonly retry = output<void>();

  readonly fmt = formatCompactTokens;
  readonly when = (at: string) => formatZonedWithUtc(at, browserTimeZone()) ?? at;
  readonly usd = formatCompactUsd;
  readonly kindLabel = kindLabel;
  readonly kindColor = kindColor;
  readonly duration = formatDuration;
  readonly outcomeTone = cardOutcomeTone;
  readonly resultTone = stepResultTone;

  readonly events = computed(() => eventShares(this.detail()?.events ?? []));
  readonly kinds = computed(() => {
    const card = this.detail()?.card;
    if (!card) return [];
    const cost = kindTotals(card, 'cost');
    return [...kindTotals(card).entries()]
      .sort((a, b) => (cost.get(b[0]) ?? 0) - (cost.get(a[0]) ?? 0) || b[1] - a[1])
      .map(([kind, total]) => ({
        kind, total, costUsd: cost.get(kind) ?? 0, color: kindColor(kind),
        share: card.totalTokens > 0 ? Math.round((total / card.totalTokens) * 100) : 0,
      }));
  });
}
