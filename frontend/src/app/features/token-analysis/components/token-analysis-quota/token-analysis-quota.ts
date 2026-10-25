import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { DatePipe } from '@angular/common';
import { formatCompactTokens } from '../../../tokens/token-number-format.util';
import type { TokenAnalysisQuotaWindow } from '../../models/token-analysis.model';
import type { QuotaLine } from '../../token-analysis.util';

interface SegmentRow {
  key: string; label: string; from: string; to: string; delta: number;
  tokens: number; perPct: number | null; uncachedPerPct: number | null; open: boolean;
}

/**
 * Provider quota context under the time chart (AGT-3267): the recorded
 * window readings on the same day axis, and tokens per quota percentage
 * point per window. A falling ratio for comparable work is a shrinking
 * effective plan limit. Shares the x axis, never the y axis.
 */
@Component({
  selector: 'app-token-analysis-quota',
  standalone: true,
  imports: [DatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './token-analysis-quota.html',
  styleUrl: './token-analysis-quota.scss',
})
export class TokenAnalysisQuotaComponent {
  readonly lines = input.required<QuotaLine[]>();
  readonly days = input.required<number>();
  readonly windows = input.required<TokenAnalysisQuotaWindow[]>();
  readonly cli = input<string | null>(null);

  readonly fmt = formatCompactTokens;
  readonly viewBox = computed(() => `0 0 ${Math.max(1, this.days())} 100`);

  readonly paths = computed(() => this.lines().map(line => ({
    ...line,
    d: line.points.map((p, i) => `${i === 0 ? 'M' : 'L'}${p.x.toFixed(3)} ${(100 - p.y).toFixed(2)}`).join(' '),
    last: line.points.at(-1)?.y ?? null,
  })));

  readonly rows = computed<SegmentRow[]>(() => this.windows()
    .filter(window => !this.cli() || window.cli === this.cli())
    .flatMap(window => window.segments.map(segment => ({
      key: `${window.cli}/${window.window}/${segment.from}`,
      label: `${window.cli} ${window.window}`,
      from: segment.from, to: segment.to,
      delta: Math.round((segment.endPct - segment.startPct) * 10) / 10,
      tokens: segment.tokens, perPct: segment.tokensPerPct, uncachedPerPct: segment.uncachedTokensPerPct,
      open: segment.open,
    })))
    .sort((a, b) => b.to.localeCompare(a.to))
    .slice(0, 16));
}
