export const MODEL_USAGE_PREFERENCES_KEY = 'atp.tokens.modelUsage.v1';
export const DEFAULT_MODEL_SHARE_THRESHOLD_PERCENT = 1;

export interface RawModelUsageRow {
  model: string;
  modelId?: string | null;
  source: string;
  calls?: number;
  cacheIncludedInInput: boolean;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;
  estimatedApiCostUsd: number;
  modelPriced: boolean;
}

export interface ModelUsageRow {
  kind: 'model';
  id: string;
  model: string;
  source: string;
  calls: number;
  cacheIncludedInInput: boolean;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;
  estimatedApiCostUsd: number;
  modelPriced: boolean;
  totalTokens: number;
}

export interface OtherModelUsageRow {
  kind: 'other';
  id: 'group:other-models';
  modelCount: number;
  unpricedModelCount: number;
  calls: number;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;
  estimatedApiCostUsd: number;
  modelPriced: boolean;
  totalTokens: number;
  rows: readonly ModelUsageRow[];
}

export interface ModelUsageProjection {
  primaryRows: readonly ModelUsageRow[];
  otherRow: OtherModelUsageRow | null;
  totalTokens: number;
  estimatedApiCostUsd: number;
  modelCount: number;
  unpricedModelCount: number;
}

export interface ModelUsagePreferences {
  thresholdPercent: number;
  expanded: boolean;
}

/** Model ids are case-insensitive; prefer the backend's canonical id. */
export function canonicalUsageModelId(model: string | null | undefined): string {
  return (model ?? '').trim().toLocaleLowerCase('en-US');
}

/** Merge split legacy buckets without combining their distinct source labels. */
export function normalizeModelUsageRows(rows: readonly RawModelUsageRow[]): ModelUsageRow[] {
  const merged = new Map<string, ModelUsageRow>();
  for (const raw of rows) {
    const model = canonicalUsageModelId(raw.modelId ?? raw.model);
    if (!model) continue;
    const id = `model:${raw.source}:${model}`;
    const totalTokens = usageRowTotal(raw);
    if (totalTokens <= 0) continue;
    const existing = merged.get(id);
    if (!existing) {
      merged.set(id, {
        kind: 'model',
        id,
        model,
        source: raw.source,
        calls: finite(raw.calls),
        cacheIncludedInInput: raw.cacheIncludedInInput,
        inputTokens: finite(raw.inputTokens),
        outputTokens: finite(raw.outputTokens),
        cacheReadTokens: finite(raw.cacheReadTokens),
        cacheCreationTokens: finite(raw.cacheCreationTokens),
        estimatedApiCostUsd: finite(raw.estimatedApiCostUsd),
        modelPriced: raw.modelPriced,
        totalTokens,
      });
      continue;
    }

    existing.calls += finite(raw.calls);
    existing.inputTokens += finite(raw.inputTokens);
    existing.outputTokens += finite(raw.outputTokens);
    existing.cacheReadTokens += finite(raw.cacheReadTokens);
    existing.cacheCreationTokens += finite(raw.cacheCreationTokens);
    existing.estimatedApiCostUsd += finite(raw.estimatedApiCostUsd);
    existing.modelPriced = existing.modelPriced && raw.modelPriced;
    existing.totalTokens += totalTokens;
  }
  return [...merged.values()].sort((a, b) =>
    b.totalTokens - a.totalTokens || a.model.localeCompare(b.model) || a.source.localeCompare(b.source));
}

/** Group every source row for a low-share canonical model together. */
export function projectModelUsageRows(
  rows: readonly ModelUsageRow[],
  thresholdPercent: number,
): ModelUsageProjection {
  const totalTokens = rows.reduce((sum, row) => sum + row.totalTokens, 0);
  const modelTotals = new Map<string, number>();
  for (const row of rows) modelTotals.set(row.model, (modelTotals.get(row.model) ?? 0) + row.totalTokens);

  const threshold = clampThreshold(thresholdPercent);
  const groupedModels = new Set<string>();
  if (threshold > 0 && totalTokens > 0) {
    for (const [model, tokens] of modelTotals) {
      if ((tokens / totalTokens) * 100 < threshold) groupedModels.add(model);
    }
  }

  const primaryRows = rows.filter(row => !groupedModels.has(row.model));
  const otherRows = rows.filter(row => groupedModels.has(row.model));
  const unpricedModels = new Set(rows.filter(row => !row.modelPriced).map(row => row.model));
  return {
    primaryRows,
    otherRow: otherRows.length > 0 ? aggregateOtherRows(otherRows) : null,
    totalTokens,
    estimatedApiCostUsd: rows.reduce((sum, row) => sum + row.estimatedApiCostUsd, 0),
    modelCount: modelTotals.size,
    unpricedModelCount: unpricedModels.size,
  };
}

export function readModelUsagePreferences(): ModelUsagePreferences {
  try {
    const parsed = JSON.parse(globalThis.localStorage?.getItem(MODEL_USAGE_PREFERENCES_KEY) ?? 'null') as unknown;
    if (!isRecord(parsed)) return defaultPreferences();
    return {
      thresholdPercent: typeof parsed['thresholdPercent'] === 'number'
        ? clampThreshold(parsed['thresholdPercent'])
        : DEFAULT_MODEL_SHARE_THRESHOLD_PERCENT,
      expanded: parsed['expanded'] === true,
    };
  } catch {
    return defaultPreferences();
  }
}

export function writeModelUsagePreferences(preferences: ModelUsagePreferences): void {
  try {
    globalThis.localStorage?.setItem(MODEL_USAGE_PREFERENCES_KEY, JSON.stringify({
      thresholdPercent: clampThreshold(preferences.thresholdPercent),
      expanded: preferences.expanded,
    }));
  } catch {
    // Storage may be blocked or full; the live signal still owns this session.
  }
}

export function clampThreshold(value: number): number {
  if (!Number.isFinite(value)) return DEFAULT_MODEL_SHARE_THRESHOLD_PERCENT;
  return Math.round(Math.max(0, Math.min(100, value)) * 10) / 10;
}

function usageRowTotal(row: RawModelUsageRow): number {
  return finite(row.inputTokens)
    + finite(row.outputTokens)
    + finite(row.cacheCreationTokens)
    + (row.cacheIncludedInInput ? 0 : finite(row.cacheReadTokens));
}

function aggregateOtherRows(rows: readonly ModelUsageRow[]): OtherModelUsageRow {
  const models = new Set(rows.map(row => row.model));
  const unpricedModels = new Set(rows.filter(row => !row.modelPriced).map(row => row.model));
  return {
    kind: 'other',
    id: 'group:other-models',
    modelCount: models.size,
    unpricedModelCount: unpricedModels.size,
    calls: rows.reduce((sum, row) => sum + row.calls, 0),
    inputTokens: rows.reduce((sum, row) => sum + row.inputTokens, 0),
    outputTokens: rows.reduce((sum, row) => sum + row.outputTokens, 0),
    cacheReadTokens: rows.reduce((sum, row) => sum + row.cacheReadTokens, 0),
    cacheCreationTokens: rows.reduce((sum, row) => sum + row.cacheCreationTokens, 0),
    estimatedApiCostUsd: rows.reduce((sum, row) => sum + row.estimatedApiCostUsd, 0),
    modelPriced: unpricedModels.size === 0,
    totalTokens: rows.reduce((sum, row) => sum + row.totalTokens, 0),
    rows,
  };
}

function finite(value: number | null | undefined): number {
  return Number.isFinite(value) ? value! : 0;
}

function defaultPreferences(): ModelUsagePreferences {
  return { thresholdPercent: DEFAULT_MODEL_SHARE_THRESHOLD_PERCENT, expanded: false };
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}
