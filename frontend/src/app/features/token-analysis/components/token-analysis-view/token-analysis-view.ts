import {
  ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, signal, untracked,
} from '@angular/core';
import type { Subscription } from 'rxjs';
import { kvValueOf, withKvSegment } from '../../../../services/url-hash.util';
import { formatCompactTokens, formatCompactUsd } from '../../../tokens/token-number-format.util';
import type { TokenAnalysisCardDetail, TokenAnalysisResponse } from '../../models/token-analysis.model';
import { TokenAnalysisApiService } from '../../services/token-analysis-api.service';
import {
  ANALYSIS_HASH_KEY, DEFAULT_TOP_N, activeKinds, allSlices, decodeAnalysisFilters, distribution,
  encodeAnalysisFilters, filterCards, filterOptions, filterRemainders, parseCardId, quotaLines, scatterPoints,
  stableIdentities, timeChart, tokenMix, topCardStacks, type TokenAnalysisFilters,
} from '../../token-analysis.util';
import { CardTokenDrilldownComponent } from '../card-token-drilldown/card-token-drilldown';
import { TokenAnalysisCardTableComponent } from '../token-analysis-card-table/token-analysis-card-table';
import { TokenAnalysisDistributionComponent } from '../token-analysis-distribution/token-analysis-distribution';
import { TokenAnalysisFiltersComponent } from '../token-analysis-filters/token-analysis-filters';
import { TokenAnalysisKindBarsComponent } from '../token-analysis-kind-bars/token-analysis-kind-bars';
import { TokenAnalysisQuotaComponent } from '../token-analysis-quota/token-analysis-quota';
import { TokenAnalysisScatterComponent } from '../token-analysis-scatter/token-analysis-scatter';
import { TokenAnalysisTimeChartComponent } from '../token-analysis-time-chart/token-analysis-time-chart';

/**
 * Card-centric token analysis (AGT-3267): which cards spent tokens on
 * which usage kinds, models, reasoning levels, CLIs and hosts, how that
 * moves over time against the provider quota, and a per-card drill-down.
 * Workspace scope in the Token usage settings; project scope when
 * `fixedProject` is set. Filter state lives in the `ta=` hash segment.
 */
@Component({
  selector: 'app-token-analysis-view',
  standalone: true,
  imports: [
    TokenAnalysisFiltersComponent, TokenAnalysisKindBarsComponent, TokenAnalysisTimeChartComponent,
    TokenAnalysisQuotaComponent, TokenAnalysisScatterComponent, TokenAnalysisDistributionComponent,
    TokenAnalysisCardTableComponent, CardTokenDrilldownComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './token-analysis-view.html',
  styleUrl: './token-analysis-view.scss',
})
export class TokenAnalysisViewComponent {
  private readonly api = inject(TokenAnalysisApiService);

  readonly fixedProject = input<string | null>(null);

  readonly filters = signal<TokenAnalysisFilters>(decodeAnalysisFilters(kvValueOf(window.location.hash, ANALYSIS_HASH_KEY)));
  readonly data = signal<TokenAnalysisResponse | null>(null);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly detail = signal<TokenAnalysisCardDetail | null>(null);
  readonly detailLoading = signal(false);
  readonly detailError = signal<string | null>(null);

  readonly fmt = formatCompactTokens;
  readonly usd = formatCompactUsd;

  /** Filters with the project scope applied. */
  readonly effective = computed<TokenAnalysisFilters>(() => {
    const fixed = this.fixedProject();
    return fixed ? { ...this.filters(), project: fixed } : this.filters();
  });

  /** Primitive keys, so a filter change refetches only when the range or the drill-down card changes. */
  private readonly days = computed(() => this.effective().days);
  private readonly drillDown = computed(() => this.effective().card);

  readonly cards = computed(() => this.data()?.cards ?? []);
  readonly options = computed(() => filterOptions(this.cards()));
  readonly visible = computed(() => filterCards(this.cards(), this.effective()));
  readonly remainders = computed(() => filterRemainders(this.data()?.remainders ?? [], this.effective()));
  readonly kinds = computed(() => activeKinds([...this.visible(), ...this.remainders()]));
  readonly stacks = computed(() => topCardStacks(this.visible(), DEFAULT_TOP_N));
  readonly identities = computed(() => stableIdentities(this.cards()));
  readonly chart = computed(() => {
    const data = this.data();
    return timeChart(this.visible(), data?.from ?? '', data?.to ?? '', this.identities());
  });
  readonly quota = computed(() => quotaLines(this.data()?.quota ?? [], this.chart().days, this.effective().cli));
  readonly scatter = computed(() => scatterPoints(this.visible()));
  readonly levels = computed(() => distribution(this.visible(), 'level'));
  readonly models = computed(() => distribution(this.visible(), 'model'));
  readonly mix = computed(() => tokenMix(allSlices(this.visible())));
  readonly inFlight = computed(() => this.visible().filter(card => card.inFlight).length);
  readonly totals = computed(() => ({
    tokens: this.visible().reduce((sum, card) => sum + card.totalTokens, 0)
      + this.remainders().reduce((sum, r) => sum + r.totalTokens, 0),
    cost: this.visible().reduce((sum, card) => sum + card.costUsd, 0)
      + this.remainders().reduce((sum, r) => sum + r.costUsd, 0),
  }));
  readonly freshnessWarning = computed(() => {
    const freshness = this.data()?.freshness;
    return freshness && freshness.status !== 'complete' ? freshness.warning || 'Some token sources could not be read.' : null;
  });

  private request?: Subscription;
  private detailRequest?: Subscription;

  constructor() {
    const onHash = () => {
      const next = decodeAnalysisFilters(kvValueOf(window.location.hash, ANALYSIS_HASH_KEY));
      if (encodeAnalysisFilters(next) !== encodeAnalysisFilters(this.filters())) this.filters.set(next);
    };
    window.addEventListener('hashchange', onHash);
    inject(DestroyRef).onDestroy(() => {
      window.removeEventListener('hashchange', onHash);
      this.request?.unsubscribe();
      this.detailRequest?.unsubscribe();
    });

    effect(() => {
      const days = this.days();
      const project = this.fixedProject();
      untracked(() => this.load(days, project));
    });
    effect(() => {
      const card = parseCardId(this.drillDown());
      untracked(() => this.loadDetail(card));
    });
  }

  setFilters(next: TokenAnalysisFilters): void {
    this.filters.set(this.fixedProject() ? { ...next, project: null } : next);
    const target = withKvSegment(window.location.hash, ANALYSIS_HASH_KEY, encodeAnalysisFilters(this.filters()));
    if (target !== window.location.hash) {
      history.replaceState(history.state, '', window.location.pathname + window.location.search + target);
    }
  }

  selectCard(id: string): void {
    this.setFilters({ ...this.filters(), card: this.filters().card === id ? null : id });
  }

  reload(): void {
    this.load(this.effective().days, this.fixedProject());
  }

  retryDetail(): void {
    this.loadDetail(parseCardId(this.effective().card));
  }

  private load(days: number, project: string | null): void {
    this.request?.unsubscribe();
    this.loading.set(true);
    this.error.set(null);
    this.request = this.api.getAnalysis(days, project).subscribe({
      next: data => { this.data.set(data); this.loading.set(false); },
      error: () => {
        this.loading.set(false);
        this.error.set('The token analysis could not be loaded.');
      },
    });
  }

  private loadDetail(card: { project: string; jobId: string } | null): void {
    this.detailRequest?.unsubscribe();
    this.detailError.set(null);
    if (!card) {
      this.detail.set(null);
      this.detailLoading.set(false);
      return;
    }
    this.detailLoading.set(true);
    this.detailRequest = this.api.getCardDetail(card.project, card.jobId).subscribe({
      next: detail => { this.detail.set(detail); this.detailLoading.set(false); },
      error: (failure: { status?: number }) => {
        this.detail.set(null);
        this.detailLoading.set(false);
        this.detailError.set(failure?.status === 404
          ? 'No token activity is recorded for this card.'
          : 'The card timeline could not be loaded.');
      },
    });
  }
}
