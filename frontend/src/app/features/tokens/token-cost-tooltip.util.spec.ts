import { describe, expect, it } from 'vitest';
import {
  TOKEN_COST_ESTIMATE_NOTICE,
  buildTokenCostTooltip,
  formatTokenCostDisplay,
  incompleteTokenCostLabel,
  formatTokenCostUsd,
} from './token-cost-tooltip.util';

describe('token cost tooltip', () => {
  it('renders a priced historical estimate with the mandatory caveat', () => {
    const tooltip = buildTokenCostTooltip({
      costUsd: 1.234,
      priceKnown: true,
      context: '12,000 tokens at execution time.',
    });

    expect(tooltip).toContain('Estimated cost: $1.23');
    expect(tooltip).toContain('at execution time');
    expect(tooltip).toContain(TOKEN_COST_ESTIMATE_NOTICE);
  });

  it('groups ordinary currency values through the shared formatter', () => {
    expect(formatTokenCostUsd(59_996.74, 'en-US')).toBe('$59,996.74');
  });

  it('says no price data instead of presenting a silent zero', () => {
    const tooltip = buildTokenCostTooltip({
      costUsd: 0,
      priceKnown: false,
      pricingGaps: [{ modelId: 'gpt-5.6-sol', reason: 'NoPriceForDate', affectedRuns: 1 }],
    });

    expect(tooltip).toContain('Estimated cost: no price data');
    expect(tooltip).toContain('gpt-5.6-sol');
    expect(tooltip).toContain('NoPriceForDate');
    expect(tooltip).toContain('No price was available for the run date');
    expect(tooltip).toContain(TOKEN_COST_ESTIMATE_NOTICE);
  });

  it('marks an aggregate with priced and unpriced calls as partial', () => {
    expect(buildTokenCostTooltip({ costUsd: 0.5, priceKnown: false, unpricedRuns: 2 }))
      .toContain('$0.50 (partial; some usage has no price data)');
  });

  it('uses a zero-dollar label only when zero tokens were consumed', () => {
    expect(formatTokenCostDisplay({ costUsd: 0, totalTokens: 0, unpricedRuns: 0 })).toBe('$0.00');
    expect(formatTokenCostDisplay({ costUsd: 0, totalTokens: 1, unpricedRuns: 0 })).toBe('<$0.0001');
    expect(formatTokenCostDisplay({ costUsd: 0, totalTokens: 700, unpricedRuns: 1 }))
      .toBe('- no price data');
  });

  it('formats the required visible marker for mixed aggregates', () => {
    expect(incompleteTokenCostLabel(1)).toBe('incomplete (1 run without price)');
    expect(incompleteTokenCostLabel(3)).toBe('incomplete (3 runs without price)');
  });
});
