import type { CliType } from '../../../models/task.model';
import type { AdHocUsageAggregate, TokenSummaryAggregate } from '../models/tokens.model';

export interface ModelUsageRow {
  model: string;
  modelId: string;
  cli: string | null;
  source: string;
  calls: number;
  cacheIncludedInInput: boolean;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;
  estimatedApiCostUsd: number;
  modelPriced: boolean;
  isOtherSummary?: boolean;
  otherCount?: number;
  isOtherChild?: boolean;
}

/** Build recorded rows from the CLI supplied by the backend. */
export function modelUsageRows(
  cli: CliType,
  tokens: TokenSummaryAggregate | null,
  adhoc: AdHocUsageAggregate | null,
  runtimeSource: string,
  countCachedInputOnce = true,
): ModelUsageRow[] {
  const rows: ModelUsageRow[] = [];
  for (const [models, source] of [
    [tokens?.byModel ?? [], runtimeSource],
    [adhoc?.byModel ?? [], 'ad-hoc'],
  ] as const) {
    for (const model of models) {
      if (model.cli !== cli) continue;
      rows.push({ ...model, source, cacheIncludedInInput: countCachedInputOnce && cli === 'codex' });
    }
  }
  return rows.sort((a, b) => modelUsageTokens(b) - modelUsageTokens(a));
}

export function modelUsageTokens(row: ModelUsageRow): number {
  return row.inputTokens + row.outputTokens + row.cacheCreationTokens
    + (row.cacheIncludedInInput ? 0 : row.cacheReadTokens);
}
