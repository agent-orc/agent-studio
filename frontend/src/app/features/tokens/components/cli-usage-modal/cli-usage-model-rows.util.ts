export const DEFAULT_MODEL_GROUP_THRESHOLD_PCT = 1;
export const MODEL_GROUP_THRESHOLD_STORAGE_KEY = 'tokenUsage.modelGroupingThresholdPct';
export const OTHER_MODELS_EXPANDED_STORAGE_KEY = 'tokenUsage.otherModelsExpanded';

export interface ModelUsageRow {
  model: string;
  source: string;
  /** OpenAI reports cached input as a subset of input, while Anthropic
   * reports cache-read tokens as a separate category. */
  cacheIncludedInInput: boolean;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;
  estimatedApiCostUsd: number;
  modelPriced: boolean;
}

export interface ModelUsageTotals {
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;
  cacheTokens: number;
  costUsd: number;
  tokens: number;
  models: number;
  unpricedModels: number;
  anyPriced: boolean;
  allPriced: boolean;
}

export interface GroupedModelUsageRows {
  primaryRows: readonly ModelUsageRow[];
  otherRows: readonly ModelUsageRow[];
  otherTotals: ModelUsageTotals;
}

export function modelUsageTokenTotal(row: ModelUsageRow): number {
  return row.inputTokens
    + row.outputTokens
    + row.cacheCreationTokens
    + (row.cacheIncludedInInput ? 0 : row.cacheReadTokens);
}

export function sumModelUsageRows(rows: readonly ModelUsageRow[]): ModelUsageTotals {
  let inputTokens = 0;
  let outputTokens = 0;
  let cacheReadTokens = 0;
  let cacheCreationTokens = 0;
  let costUsd = 0;
  let tokens = 0;
  const models = new Set<string>();
  const unpricedModels = new Set<string>();

  for (const row of rows) {
    inputTokens += row.inputTokens;
    outputTokens += row.outputTokens;
    cacheReadTokens += row.cacheReadTokens;
    cacheCreationTokens += row.cacheCreationTokens;
    tokens += modelUsageTokenTotal(row);
    costUsd += row.estimatedApiCostUsd;
    const modelKey = modelUsageKey(row);
    models.add(modelKey);
    if (!row.modelPriced) unpricedModels.add(modelKey);
  }

  return {
    inputTokens,
    outputTokens,
    cacheReadTokens,
    cacheCreationTokens,
    cacheTokens: cacheReadTokens + cacheCreationTokens,
    costUsd,
    tokens,
    models: models.size,
    unpricedModels: unpricedModels.size,
    anyPriced: rows.some(row => row.modelPriced || row.estimatedApiCostUsd !== 0),
    allPriced: rows.length > 0 && unpricedModels.size === 0,
  };
}

export function groupModelUsageRows(
  rows: readonly ModelUsageRow[],
  thresholdPct: number,
): GroupedModelUsageRows {
  const totalTokens = sumModelUsageRows(rows).tokens;
  const threshold = normalizeModelGroupThreshold(thresholdPct);
  if (threshold <= 0 || totalTokens <= 0) {
    return { primaryRows: rows, otherRows: [], otherTotals: sumModelUsageRows([]) };
  }

  const tokensByModel = new Map<string, number>();
  for (const row of rows) {
    const key = modelUsageKey(row);
    tokensByModel.set(key, (tokensByModel.get(key) ?? 0) + modelUsageTokenTotal(row));
  }
  const otherRows = rows.filter(row =>
    ((tokensByModel.get(modelUsageKey(row)) ?? 0) / totalTokens) * 100 < threshold);
  const other = new Set(otherRows);
  return {
    primaryRows: rows.filter(row => !other.has(row)),
    otherRows,
    otherTotals: sumModelUsageRows(otherRows),
  };
}

function modelUsageKey(row: ModelUsageRow): string {
  return row.model.trim().toLowerCase();
}

export function normalizeModelGroupThreshold(value: unknown): number {
  const parsed = typeof value === 'number' ? value : Number.parseFloat(String(value));
  return Number.isFinite(parsed) ? Math.min(100, Math.max(0, parsed)) : DEFAULT_MODEL_GROUP_THRESHOLD_PCT;
}

export function readModelGroupThreshold(): number {
  try {
    const stored = globalThis.localStorage?.getItem(MODEL_GROUP_THRESHOLD_STORAGE_KEY);
    return stored === null || stored === undefined
      ? DEFAULT_MODEL_GROUP_THRESHOLD_PCT
      : normalizeModelGroupThreshold(stored);
  } catch {
    return DEFAULT_MODEL_GROUP_THRESHOLD_PCT;
  }
}

export function writeModelGroupThreshold(value: number): void {
  try { globalThis.localStorage?.setItem(MODEL_GROUP_THRESHOLD_STORAGE_KEY, String(value)); }
  catch { /* Browser storage is best-effort; the signal remains authoritative. */ }
}

export function readOtherModelsExpanded(): boolean {
  try { return globalThis.localStorage?.getItem(OTHER_MODELS_EXPANDED_STORAGE_KEY) === '1'; }
  catch { return false; }
}

export function writeOtherModelsExpanded(expanded: boolean): void {
  try { globalThis.localStorage?.setItem(OTHER_MODELS_EXPANDED_STORAGE_KEY, expanded ? '1' : '0'); }
  catch { /* Browser storage is best-effort; the signal remains authoritative. */ }
}
