import { beforeEach, describe, expect, it } from 'vitest';
import {
  DEFAULT_MODEL_SHARE_THRESHOLD_PERCENT,
  MODEL_USAGE_PREFERENCES_KEY,
  normalizeModelUsageRows,
  projectModelUsageRows,
  readModelUsagePreferences,
  writeModelUsagePreferences,
  type RawModelUsageRow,
} from './model-usage-table.util';

function row(overrides: Partial<RawModelUsageRow>): RawModelUsageRow {
  return {
    model: 'gpt-5.6-sol',
    source: 'project runtime',
    calls: 1,
    cacheIncludedInInput: true,
    inputTokens: 1_000,
    outputTokens: 0,
    cacheReadTokens: 0,
    cacheCreationTokens: 0,
    estimatedApiCostUsd: 1,
    modelPriced: true,
    ...overrides,
  };
}

describe('model usage table projection', () => {
  beforeEach(() => localStorage.removeItem(MODEL_USAGE_PREFERENCES_KEY));

  it('prefers modelId and merges case-only legacy buckets within a source', () => {
    const rows = normalizeModelUsageRows([
      row({ model: 'GPT-5.5', inputTokens: 600 }),
      row({ model: 'display label', modelId: 'gpt-5.5', inputTokens: 400 }),
    ]);

    expect(rows).toHaveLength(1);
    expect(rows[0]).toMatchObject({ model: 'gpt-5.5', inputTokens: 1_000, totalTokens: 1_000 });
  });

  it('thresholds the combined model share while retaining source rows for expansion', () => {
    const rows = normalizeModelUsageRows([
      row({ model: 'gpt-main', inputTokens: 98_500 }),
      row({ model: 'gpt-small', source: 'project runtime', inputTokens: 700 }),
      row({ model: 'gpt-small', source: 'ad-hoc', inputTokens: 300 }),
      row({ model: 'gpt-tiny', inputTokens: 500, modelPriced: false, estimatedApiCostUsd: 2.5 }),
    ]);
    const projection = projectModelUsageRows(rows, 1);

    expect(projection.primaryRows.map(item => item.model)).toEqual(['gpt-main', 'gpt-small', 'gpt-small']);
    expect(projection.otherRow).toMatchObject({
      modelCount: 1,
      totalTokens: 500,
      estimatedApiCostUsd: 2.5,
      unpricedModelCount: 1,
    });
    expect(projection.otherRow?.rows).toHaveLength(1);
    expect(projection.totalTokens).toBe(100_000);
    expect(projection.estimatedApiCostUsd).toBe(5.5);
    expect(projection.modelCount).toBe(3);
    expect(projection.unpricedModelCount).toBe(1);
  });

  it('round-trips the viewer threshold and expanded state', () => {
    expect(readModelUsagePreferences()).toEqual({
      thresholdPercent: DEFAULT_MODEL_SHARE_THRESHOLD_PERCENT,
      expanded: false,
    });

    writeModelUsagePreferences({ thresholdPercent: 2.4, expanded: true });

    expect(readModelUsagePreferences()).toEqual({ thresholdPercent: 2.4, expanded: true });
  });
});
