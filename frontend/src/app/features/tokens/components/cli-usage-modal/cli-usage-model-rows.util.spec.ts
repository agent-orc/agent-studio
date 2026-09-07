import { describe, expect, it } from 'vitest';
import {
  type ModelUsageRow,
  groupModelUsageRows,
  sumModelUsageRows,
} from './cli-usage-model-rows.util';

function row(
  model: string,
  source: string,
  inputTokens: number,
  overrides: Partial<ModelUsageRow> = {},
): ModelUsageRow {
  return {
    model,
    source,
    cacheIncludedInInput: true,
    inputTokens,
    outputTokens: 0,
    cacheReadTokens: 0,
    cacheCreationTokens: 0,
    estimatedApiCostUsd: 0,
    modelPriced: true,
    ...overrides,
  };
}

describe('CLI usage model row grouping', () => {
  it('retains the known subtotal from an incompletely priced row', () => {
    const totals = sumModelUsageRows([
      row('gpt-5.5', 'project runtime', 10, {
        estimatedApiCostUsd: 12.34,
        modelPriced: false,
      }),
    ]);

    expect(totals.costUsd).toBe(12.34);
    expect(totals.unpricedModels).toBe(1);
    expect(totals.anyPriced).toBe(true);
  });

  it('counts one unpriced model across duplicate source rows', () => {
    const totals = sumModelUsageRows([
      row('gpt-unpriced', 'project runtime', 10, { modelPriced: false }),
      row('GPT-UNPRICED', 'ad-hoc', 5, { modelPriced: false }),
    ]);

    expect(totals.models).toBe(1);
    expect(totals.unpricedModels).toBe(1);
    expect(totals.allPriced).toBe(false);
  });

  it('groups by combined model share and counts duplicate source rows once', () => {
    const rows = [
      row('gpt-large', 'project runtime', 983),
      row('gpt-alpha', 'project runtime', 6),
      row('GPT-ALPHA', 'ad-hoc', 6),
      row('gpt-small', 'project runtime', 2),
      row('gpt-small', 'ad-hoc', 3),
    ];

    const grouped = groupModelUsageRows(rows, 1);

    expect(grouped.primaryRows.map(item => item.model))
      .toEqual(['gpt-large', 'gpt-alpha', 'GPT-ALPHA']);
    expect(grouped.otherRows.map(item => item.model))
      .toEqual(['gpt-small', 'gpt-small']);
    expect(grouped.otherTotals.models).toBe(1);
    expect(sumModelUsageRows(rows).models).toBe(3);
  });
});
