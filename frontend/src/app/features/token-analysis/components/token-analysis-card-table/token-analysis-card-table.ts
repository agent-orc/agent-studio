import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { AppTooltipDirective } from '../../../../components/tooltip/app-tooltip.directive';
import { laneName } from '../../../../models/lane-presentation';
import { formatCompactTokens, formatCompactUsd } from '../../../tokens/token-number-format.util';
import type { TokenAnalysisCard, TokenAnalysisProjectRemainder } from '../../models/token-analysis.model';
import { cardId, dominant, kindColor, kindLabel, kindTotals } from '../../token-analysis.util';

const PAGE = 50;

interface Row {
  id: string;
  card: TokenAnalysisCard | null;
  label: string;
  title: string;
  project: string;
  outcome: string;
  lane: string;
  rounds: number | null;
  kinds: number[];
  split: { kind: string; value: number; color: string }[];
  model: string; level: string; cli: string; host: string;
  total: number; cost: number; costComplete: boolean; inFlight: boolean;
}

/**
 * Card table of the token analysis (AGT-3267): tokens per usage kind,
 * dominant model, reasoning level, CLI and host, rounds, outcome, and
 * theoretical cost. Project usage that belongs to no card is listed last,
 * so the column sums match the charts.
 */
@Component({
  selector: 'app-token-analysis-card-table',
  standalone: true,
  imports: [AppTooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './token-analysis-card-table.html',
  styleUrl: './token-analysis-card-table.scss',
})
export class TokenAnalysisCardTableComponent {
  readonly cards = input.required<TokenAnalysisCard[]>();
  readonly remainders = input<TokenAnalysisProjectRemainder[]>([]);
  readonly kinds = input.required<string[]>();
  readonly selectedId = input<string | null>(null);
  readonly cardSelect = output<string>();

  readonly fmt = formatCompactTokens;
  readonly usd = formatCompactUsd;
  readonly kindLabel = kindLabel;
  readonly limit = signal(PAGE);

  readonly rows = computed<Row[]>(() => {
    const kinds = this.kinds();
    const cards = this.cards().map(card => this.row(card, kinds));
    const rest = this.remainders()
      .filter(remainder => remainder.totalTokens > 0)
      .map(remainder => this.row({
        project: remainder.project, jobId: '', key: null, title: 'Usage outside cards (project chat, orchestrator)',
        state: null, taskType: '', tags: [], outcome: '', rounds: 0, inFlight: false,
        firstActivity: null, lastActivity: null, totalTokens: remainder.totalTokens, costUsd: remainder.costUsd,
        costComplete: remainder.slices.every(slice => slice.priced), slices: remainder.slices,
      }, kinds, true));
    return [...cards, ...rest];
  });

  readonly visible = computed(() => this.rows().slice(0, this.limit()));
  readonly totals = computed(() => {
    const rows = this.rows();
    return {
      kinds: this.kinds().map((_, i) => rows.reduce((sum, row) => sum + row.kinds[i], 0)),
      tokens: rows.reduce((sum, row) => sum + row.total, 0),
      cost: rows.reduce((sum, row) => sum + row.cost, 0),
    };
  });

  showAll(): void {
    this.limit.set(Number.MAX_SAFE_INTEGER);
  }

  private row(card: TokenAnalysisCard, kinds: string[], remainder = false): Row {
    const totals = kindTotals(card);
    const label = (d: { value: string; extra: number }) => d.extra > 0 ? `${d.value} +${d.extra}` : d.value;
    return {
      id: remainder ? `remainder/${card.project}` : cardId(card),
      card: remainder ? null : card,
      label: remainder ? card.project : card.key ?? card.jobId,
      title: card.title,
      project: card.project,
      outcome: card.outcome,
      lane: card.state ? laneName(card.state) : '',
      rounds: remainder ? null : card.rounds,
      kinds: kinds.map(kind => totals.get(kind) ?? 0),
      split: kinds.filter(kind => (totals.get(kind) ?? 0) > 0)
        .map(kind => ({ kind, value: totals.get(kind)!, color: kindColor(kind) })),
      model: label(dominant(card, 'model')),
      level: label(dominant(card, 'level')),
      cli: label(dominant(card, 'cli')),
      host: label(dominant(card, 'host')),
      total: card.totalTokens,
      cost: card.costUsd,
      costComplete: card.costComplete,
      inFlight: card.inFlight,
    };
  }
}
