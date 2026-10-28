import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { DatePipe } from '@angular/common';
import { formatCompactTokens, formatCompactUsd } from '../../../tokens/token-number-format.util';
import type { TokenAnalysisCardDetail } from '../../models/token-analysis.model';
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
  imports: [DatePipe],
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
  readonly usd = formatCompactUsd;
  readonly kindLabel = kindLabel;
  readonly kindColor = kindColor;
  readonly duration = formatDuration;

  readonly events = computed(() => eventShares(this.detail()?.events ?? []));
  readonly kinds = computed(() => {
    const card = this.detail()?.card;
    if (!card) return [];
    return [...kindTotals(card).entries()]
      .sort((a, b) => b[1] - a[1])
      .map(([kind, total]) => ({
        kind, total, color: kindColor(kind),
        share: card.totalTokens > 0 ? Math.round((total / card.totalTokens) * 100) : 0,
      }));
  });
}
