import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { formatCompactTokens, formatCompactUsd } from '../../../tokens/token-number-format.util';
import type { DistributionRow, TokenMix } from '../../token-analysis.util';
import { seriesColor } from '../../token-analysis.util';

/**
 * Tokens per reasoning level and per model with per-card median and 90th
 * percentile, plus the share of cached input, uncached input, cache writes,
 * output, and reported reasoning (AGT-3267).
 */
@Component({
  selector: 'app-token-analysis-distribution',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './token-analysis-distribution.html',
  styleUrl: './token-analysis-distribution.scss',
})
export class TokenAnalysisDistributionComponent {
  readonly levels = input.required<DistributionRow[]>();
  readonly models = input.required<DistributionRow[]>();
  readonly mix = input.required<TokenMix>();

  readonly fmt = formatCompactTokens;
  readonly usd = formatCompactUsd;

  readonly groups = computed(() => [
    { key: 'level', title: 'By reasoning level', rows: this.levels(), max: this.maxOf(this.levels()) },
    { key: 'model', title: 'By model', rows: this.models().slice(0, 8), max: this.maxOf(this.models()) },
  ]);

  readonly mixParts = computed(() => {
    const mix = this.mix();
    const parts = [
      { key: 'cached', label: 'Cached input', value: mix.cachedInput },
      { key: 'uncached', label: 'Uncached input', value: mix.uncachedInput },
      { key: 'cache-write', label: 'Cache write', value: mix.cacheWrite },
      { key: 'output', label: 'Output', value: mix.output },
      { key: 'reasoning', label: 'Reasoning', value: mix.reasoning },
    ];
    return parts.map((part, i) => ({
      ...part, color: seriesColor(i), share: mix.total > 0 ? Math.round((part.value / mix.total) * 1000) / 10 : 0,
    }));
  });

  readonly reasoningCoverage = computed(() => {
    const mix = this.mix();
    const output = mix.output + mix.reasoning;
    return output > 0 ? Math.round((mix.reasoningReportedOutput / output) * 100) : 0;
  });

  width(value: number, max: number): string {
    return max > 0 ? `${(value / max) * 100}%` : '0%';
  }

  private maxOf(rows: DistributionRow[]): number {
    return rows.reduce((m, row) => Math.max(m, row.tokens), 0);
  }
}
