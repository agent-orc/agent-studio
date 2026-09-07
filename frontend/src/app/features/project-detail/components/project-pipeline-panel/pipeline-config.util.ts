/**
 * Pure helpers + vocabulary for the project-level Pipeline page. Kept out
 * of the component so the ordering / sectioning logic stays testable and
 * the component controller stays under its size budget. Nothing here
 * touches Angular — it operates on the catalogue + order arrays directly.
 */
import type { PipelineCatalogueStep, PipelineStepSetting, PipelineType } from '../../../task-pipeline';
import type { ProjectPipelineCostTimeline } from '../../../project-token-usage';
import { buildTokenCostTooltip, type TokenPricingGap } from '../../../tokens';

/** Pipeline type selector, ordered per the settings convention. */
export const PIPELINE_TYPES: readonly { id: PipelineType; label: string; hint: string }[] = [
  { id: 'task', label: 'Task', hint: 'Default chain for chores and technical work.' },
  { id: 'bug', label: 'Bug', hint: 'Chain used by cards classified as bugs.' },
  { id: 'feature', label: 'Feature', hint: 'Chain used by feature cards.' },
  { id: 'planning', label: 'Planning', hint: 'Lightweight read-only chain used by planning and research cards.' },
];

export function pipelineTypeOverrides(
  settings: {
    pipelineSteps?: Record<string, PipelineStepSetting>;
    pipelineStepOrder?: string[];
    pipelineStepsByType?: Record<string, Record<string, PipelineStepSetting>>;
    pipelineStepOrderByType?: Record<string, string[]>;
  } | undefined,
  type: PipelineType,
): { steps: Record<string, PipelineStepSetting>; order: string[] } {
  const legacySteps = type === 'task' ? settings?.pipelineSteps : undefined;
  const legacyOrder = type === 'task' ? settings?.pipelineStepOrder : undefined;
  return {
    steps: settings?.pipelineStepsByType?.[type] ?? legacySteps ?? {},
    order: settings?.pipelineStepOrderByType?.[type] ?? legacyOrder ?? [],
  };
}

/** Gate-mode choices for steps that expose a warn/fail gate (lint, decision). */
export const PIPELINE_GATE_MODES: readonly { id: string; label: string }[] = [
  { id: '',     label: 'Default' },
  { id: 'off',  label: 'Off' },
  { id: 'warn', label: 'Warn' },
  { id: 'fail', label: 'Fail' },
];

/**
 * Run-condition choices for pipeline steps. Mirrors the backend
 * `PipelineStepConditions` vocabulary. Empty `id` clears any condition so
 * the step runs whenever it is enabled (equivalent to `always`). The
 * value-bearing tokens (`task-type`, `tag`) require a free-text value.
 */
export const PIPELINE_CONDITIONS: readonly { id: string; label: string; needsValue?: boolean }[] = [
  { id: '',                label: 'Always (when enabled)' },
  { id: 'never',           label: 'Never' },
  { id: 'on-abort',        label: 'Only on abort' },
  { id: 'on-nonzero-exit', label: 'Only on non-zero exit' },
  { id: 'on-aspect-fail',  label: 'Only when an aspect fails' },
  { id: 'task-type',       label: 'Only for task type...', needsValue: true },
  { id: 'tag',             label: 'Only for tag...',       needsValue: true },
];

/** Condition tokens that require a free-text value entered next to the select. */
export const PIPELINE_CONDITION_VALUE_TOKENS: readonly string[] = ['task-type', 'tag'];

/** Window (days) for the per-step token sum shown on each pipeline row. */
export const PIPELINE_TOKEN_WINDOW_DAYS = 90;

/** One row per configurable step: catalogue metadata joined with the override. */
export interface PipelineAdminRow {
  id: string;
  displayName: string;
  kind: string;
  appliesTo: 'angular' | 'dotnet' | 'node' | 'any';
  applicable: boolean;
  effectiveExecution: NonNullable<PipelineCatalogueStep['effectiveExecution']>;
  runMode: string;
  dependsOn: string[];
  idempotent: boolean;
  stub: boolean;
  deferred: boolean;
  usesModel: boolean;
  supportsEconomyModel: boolean;
  usesPrompt: boolean;
  supportsMode: boolean;
  canDisable: boolean;
  supportsCondition: boolean;
  framework?: string;
  phase: string;
  enabled: boolean;
  economyModel: boolean;
  cliType: string;
  model: string;
  thinkingLevel: string;
  effectiveCliType: string;
  effectiveModel: string;
  effectiveModelSource: string;
  effectiveThinkingLevel: string;
  modelMigration?: PipelineCatalogueStep['modelMigration'];
  /** Inline prompt override text (legacy). Empty = bound to the registry template. */
  prompt: string;
  /** Registry template this step renders from, when the catalogue declares one. */
  promptTemplate: string;
  mode: string;
  condition: string;
  conditionValue: string;
  conditionNeedsValue: boolean;
  canMoveUp: boolean;
  canMoveDown: boolean;
  /** Token sum this step spent in the window, or null when none was recorded. */
  tokenSum: number | null;
  /** True when the step's token sum included a model with no price on file. */
  tokenUnknown: boolean;
  /** Historical list-price sum for the priced usage rows in the window. */
  tokenCostUsd: number | null;
  /** Runs in the window with token usage but no historical price. */
  tokenUnpricedRuns?: number;
  /** Concrete model and resolver reasons for the missing historical prices. */
  tokenPricingGaps?: TokenPricingGap[];
}

export interface PipelineStepTokenCost {
  tokens: number;
  costUsd: number;
  unknown: boolean;
  unpricedRuns: number;
  pricingGaps: TokenPricingGap[];
}

/** Index the project price aggregate once before joining it to catalogue rows. */
export function pipelineTokenCostByStep(
  timeline: ProjectPipelineCostTimeline | null,
): Map<string, PipelineStepTokenCost> {
  return new Map((timeline?.steps ?? []).map(step => [step.stepId, {
    tokens: step.totalTokens,
    costUsd: step.totalCostUsd,
    unknown: step.anyModelUnknown,
    unpricedRuns: step.unpricedRuns ?? (step.anyModelUnknown ? 1 : 0),
    pricingGaps: step.pricingGaps ?? [],
  }]));
}

/** One pre/core/post phase grouping for the grid. */
export interface PipelineGroup {
  phase: string;
  label: string;
  rows: PipelineAdminRow[];
}

export function phaseForStep(step: PipelineCatalogueStep): string {
  if (step.kind === 'aspect') return 'aspect';
  if (step.kind === 'tool') return 'tool';
  if (step.kind === 'analysis') return 'analysis';
  if (step.kind === 'drift') return 'drift';
  if (step.kind === 'core') return 'core';
  if (step.id.startsWith('pre-')) return 'pre';
  if (step.id.includes('decision')) return 'decision';
  if (step.id.includes('abort')) return 'abort';
  return 'post';
}

export function pipelinePhaseLabel(phase: string): string {
  switch (phase) {
    case 'pre': return 'Pre steps';
    case 'core': return 'Core agent work';
    case 'aspect': return 'Aspect reviews';
    case 'tool': return 'Tool steps';
    case 'analysis': return 'Quality analysis';
    case 'decision': return 'Decision';
    case 'drift': return 'Drift';
    case 'abort': return 'Abort-only';
    default: return 'Post steps';
  }
}

/** The pre / core / post ordering bucket a step belongs to. */
export function pipelineOrderSection(step: PipelineCatalogueStep): 'pre' | 'core' | 'post' {
  if (step.kind === 'core') return 'core';
  if ((step.phase ?? phaseForStep(step)) === 'pre') return 'pre';
  return 'post';
}

/** Apply the persisted per-project step order to one section's steps. */
export function sortPipelineOrderSection(
  steps: readonly PipelineCatalogueStep[],
  order: readonly string[],
): readonly PipelineCatalogueStep[] {
  if (order.length === 0 || steps.length <= 1) return steps;

  const rank = new Map<string, number>();
  for (const id of order) {
    const key = id.trim().toLowerCase();
    if (key && !rank.has(key)) rank.set(key, rank.size);
  }
  if (rank.size === 0) return steps;

  return steps
    .map((step, index) => ({ step, index, rank: rank.get(step.id.toLowerCase()) ?? Number.MAX_SAFE_INTEGER }))
    .sort((a, b) => a.rank - b.rank || a.index - b.index)
    .map(x => x.step);
}

/** Catalogue re-ordered as pre (ordered) + core (fixed) + post (ordered). */
export function orderedPipelineCatalogue(
  steps: readonly PipelineCatalogueStep[],
  order: readonly string[],
): readonly PipelineCatalogueStep[] {
  const pre = sortPipelineOrderSection(steps.filter(s => pipelineOrderSection(s) === 'pre'), order);
  const core = steps.filter(s => pipelineOrderSection(s) === 'core');
  const post = sortPipelineOrderSection(steps.filter(s => pipelineOrderSection(s) === 'post'), order);
  return [...pre, ...core, ...post];
}

/** Whether the step at `index` can swap with a same-section neighbour in `direction`. */
export function canMovePipelineStep(
  steps: readonly PipelineCatalogueStep[],
  index: number,
  direction: -1 | 1,
): boolean {
  const step = steps[index];
  if (!step) return false;
  const section = pipelineOrderSection(step);
  if (section === 'core') return false;

  let target = index + direction;
  while (target >= 0 && target < steps.length) {
    if (pipelineOrderSection(steps[target]) === section) return true;
    target += direction;
  }
  return false;
}

export function formatTokens(n: number | null | undefined): string {
  const v = n ?? 0;
  const sign = v < 0 ? '-' : '';
  const abs = Math.abs(v);
  if (abs >= 1_000_000_000) return `${sign}${(abs / 1_000_000_000).toFixed(1)}B`;
  if (abs >= 1_000_000) return `${sign}${(abs / 1_000_000).toFixed(1)}M`;
  if (abs >= 1_000) return `${sign}${(abs / 1_000).toFixed(1)}k`;
  return `${sign}${abs}`;
}

/** Read-only label for a step's window token sum, e.g. "12.3k tokens / 90d". */
export function stepTokenLabel(row: Pick<PipelineAdminRow, 'tokenSum'>): string {
  const d = PIPELINE_TOKEN_WINDOW_DAYS;
  return row.tokenSum == null ? `No tokens / ${d}d` : `${formatTokens(row.tokenSum)} tokens / ${d}d`;
}

/** Verbose tooltip behind a step's token sum chip. */
export function stepTokenTooltip(row: Pick<PipelineAdminRow,
  'tokenSum' | 'tokenUnknown' | 'tokenCostUsd' | 'tokenUnpricedRuns' | 'tokenPricingGaps'>): string {
  const d = PIPELINE_TOKEN_WINDOW_DAYS;
  const context = row.tokenSum == null
    ? `No token usage recorded for this step in the last ${d} days.`
    : `${row.tokenSum.toLocaleString()} tokens spent by this step across every task run in the last ${d} days.`;
  return buildTokenCostTooltip({
    costUsd: row.tokenCostUsd,
    priceKnown: row.tokenSum != null && !row.tokenUnknown,
    totalTokens: row.tokenSum ?? 0,
    context,
    unpricedRuns: row.tokenUnpricedRuns ?? 0,
    pricingGaps: row.tokenPricingGaps ?? [],
  });
}
