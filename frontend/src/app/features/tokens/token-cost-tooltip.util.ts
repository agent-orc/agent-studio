import { formatUsageCurrency } from './usage-number-format.util';

/** Mandatory caveat shown on every token-to-money tooltip. */
export const TOKEN_COST_ESTIMATE_NOTICE =
  'Estimated - historical list prices; discounts and provider-side caching adjustments are not considered.';

export interface TokenCostTooltipOptions {
  costUsd: number | null | undefined;
  /** True only when every usage row contributing to the amount was priced. */
  priceKnown: boolean;
  /** Token count distinguishes an empty zero from a rounded tiny estimate. */
  totalTokens?: number | null;
  /** Optional token/context sentence rendered before the money estimate. */
  context?: string | null;
  /** Count of runs contributing tokens without a resolved historical price. */
  unpricedRuns?: number | null;
  /** Concrete model and resolver reason for every missing-price group. */
  pricingGaps?: readonly TokenPricingGap[] | null;
}

export interface TokenPricingGap {
  modelId: string;
  reason: string;
  affectedRuns: number;
}

export interface TokenCostDisplayOptions {
  costUsd: number | null | undefined;
  totalTokens: number;
  unpricedRuns: number;
}

/** Consistent compact USD formatting for token-cost tooltips and popovers. */
export function formatTokenCostUsd(costUsd: number, locale?: string | string[]): string {
  if (!Number.isFinite(costUsd)) return 'no price data';
  if (Math.abs(costUsd) > 0 && Math.abs(costUsd) < 0.01) return `$${costUsd.toFixed(4)}`;
  return formatUsageCurrency(costUsd, { locale });
}

/** Visible compact label. A real zero is reserved for zero-token usage. */
export function formatTokenCostDisplay(options: TokenCostDisplayOptions): string {
  const hasResolvedSubtotal = Number.isFinite(options.costUsd) && options.costUsd! > 0;
  if (options.totalTokens > 0 && !hasResolvedSubtotal) {
    if (options.unpricedRuns > 0 || !Number.isFinite(options.costUsd)) return '- no price data';
    return '<$0.0001';
  }
  return formatTokenCostUsd(Number.isFinite(options.costUsd) ? options.costUsd! : 0);
}

/** Visible marker required beside a priced subtotal in a mixed aggregate. */
export function incompleteTokenCostLabel(unpricedRuns: number): string {
  const runs = Math.max(0, Math.trunc(unpricedRuns));
  return `incomplete (${runs} run${runs === 1 ? '' : 's'} without price)`;
}

export function tokenPriceGapReason(reason: string): string {
  switch (reason.trim().toLowerCase()) {
    case 'nopricefordate':
      return 'No price was available for the run date (NoPriceForDate).';
    case 'unknownmodel':
      return 'The model is not present in the price catalog (UnknownModel).';
    default:
      return `Price resolution failed (${reason || 'unknown reason'}).`;
  }
}

/**
 * Shared token-cost tooltip contract. Unknown prices never become a silent
 * zero, partial aggregates remain explicit, and the estimate caveat is always
 * present.
 */
export function buildTokenCostTooltip(options: TokenCostTooltipOptions): string {
  const lines: string[] = [];
  if (options.context?.trim()) lines.push(options.context.trim());

  const hasCost = Number.isFinite(options.costUsd);
  if (options.priceKnown && hasCost) {
    lines.push(`Estimated cost: ${formatTokenCostDisplay({
      costUsd: options.costUsd,
      totalTokens: options.totalTokens ?? 0,
      unpricedRuns: options.unpricedRuns ?? 0,
    })}`);
  } else if (hasCost && options.costUsd! > 0) {
    lines.push(`Estimated cost: ${formatTokenCostUsd(options.costUsd!)} (partial; some usage has no price data)`);
  } else {
    lines.push('Estimated cost: no price data');
  }
  if ((options.unpricedRuns ?? 0) > 0) {
    const count = options.unpricedRuns!;
    lines.push(`Incomplete: ${count} run${count === 1 ? '' : 's'} without price.`);
  }
  for (const gap of options.pricingGaps ?? []) {
    lines.push(`${gap.modelId}: ${tokenPriceGapReason(gap.reason)} Affected runs: ${gap.affectedRuns}.`);
  }
  lines.push(TOKEN_COST_ESTIMATE_NOTICE);
  return lines.join('\n');
}
