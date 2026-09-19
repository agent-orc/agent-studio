import type { StructuredTooltip } from 'coding-agent-chat/shared';
import type { PipelineCostSummary, PipelineStepCost } from '../../../../task-pipeline';
import { buildTokenCostTooltip } from '../../../../tokens';
import { formatTokens } from './overview-pane-formatters';

export function formatPipelineCost(usd: number): string {
  if (usd <= 0) return '$0.00';
  if (usd < 0.01) return `$${usd.toFixed(4)}`;
  return `$${usd.toFixed(2)}`;
}

/** Aggregate labels must preserve unknown and partial pricing semantics. */
export function formatPipelineAggregateCost(usd: number, anyModelUnknown: boolean): string {
  if (!anyModelUnknown) return formatPipelineCost(usd);
  if (usd <= 0) return 'Unknown';
  return `${formatPipelineCost(usd)} partial`;
}

export function buildPipelineStepTokenTooltip(
  label: string,
  cost: PipelineStepCost | null,
): StructuredTooltip | null {
  if (!cost || cost.totalTokens <= 0) return null;
  const source = cost.tokenUsageSource?.trim();
  const context = [
    ...(source ? [`Source: ${source}`] : []),
    `Model: ${cost.model ?? 'unknown'}`,
    `Input: ${formatTokens(cost.inputTokens)}`,
    `Output: ${formatTokens(cost.outputTokens)}`,
    `Cache read: ${formatTokens(cost.cacheReadTokens)}`,
    `Cache creation: ${formatTokens(cost.cacheCreationTokens)}`,
    `Total: ${formatTokens(cost.totalTokens)}`,
  ];
  if (cost.modelKnown) {
    context.push(
      '',
      `Input cost: ${formatPipelineCost(cost.inputCostUsd)}`,
      `Output cost: ${formatPipelineCost(cost.outputCostUsd)}`,
      `Cache read cost: ${formatPipelineCost(cost.cacheReadCostUsd)}`,
      `Cache creation cost: ${formatPipelineCost(cost.cacheCreationCostUsd)}`,
    );
  }
  return {
    title: `${label} tokens`,
    body: buildTokenCostTooltip({
      costUsd: cost.costUsd,
      priceKnown: cost.modelKnown,
      totalTokens: cost.totalTokens,
      context: context.join('\n'),
      unpricedRuns: cost.modelKnown ? 0 : 1,
      pricingGaps: cost.pricingGaps,
    }),
  };
}

export function buildPipelineStepCostTooltip(
  label: string,
  cost: PipelineStepCost | null,
): StructuredTooltip | null {
  if (!cost || cost.totalTokens <= 0) return null;
  const context = cost.modelKnown
    ? [
        `Input: ${formatPipelineCost(cost.inputCostUsd)}`,
        `Output: ${formatPipelineCost(cost.outputCostUsd)}`,
        `Cache read: ${formatPipelineCost(cost.cacheReadCostUsd)}`,
        `Cache creation: ${formatPipelineCost(cost.cacheCreationCostUsd)}`,
      ].join('\n')
    : `Model: ${cost.model ?? 'unknown'}`;
  return {
    title: `${label} cost`,
    body: buildTokenCostTooltip({
      costUsd: cost.costUsd,
      priceKnown: cost.modelKnown,
      totalTokens: cost.totalTokens,
      context,
      unpricedRuns: cost.modelKnown ? 0 : 1,
      pricingGaps: cost.pricingGaps,
    }),
  };
}

export function buildPipelineTotalTokenTooltip(
  cost: PipelineCostSummary,
): StructuredTooltip | null {
  if (cost.totalTokens <= 0) return null;
  const context = [
    'Source: pipeline steps in this run',
    `Input: ${formatTokens(cost.totalInputTokens)}`,
    `Output: ${formatTokens(cost.totalOutputTokens)}`,
    `Cache read: ${formatTokens(cost.totalCacheReadTokens)}`,
    `Cache creation: ${formatTokens(cost.totalCacheCreationTokens)}`,
    `Total: ${formatTokens(cost.totalTokens)}`,
    '',
    `Input API price: ${formatPipelineCost(cost.totalInputCostUsd)}`,
    `Output API price: ${formatPipelineCost(cost.totalOutputCostUsd)}`,
    `Cache read API price: ${formatPipelineCost(cost.totalCacheReadCostUsd)}`,
    `Cache creation API price: ${formatPipelineCost(cost.totalCacheCreationCostUsd)}`,
  ];
  if (cost.anyModelUnknown) {
    context.push('One or more steps used a model with no price data; the estimate covers only priced usage.');
  }
  return {
    title: 'Pipeline total tokens',
    body: buildTokenCostTooltip({
      costUsd: cost.totalCostUsd,
      priceKnown: !cost.anyModelUnknown,
      totalTokens: cost.totalTokens,
      context: context.join('\n'),
      unpricedRuns: cost.unpricedRuns ?? (cost.anyModelUnknown ? 1 : 0),
      pricingGaps: cost.pricingGaps,
    }),
  };
}

export function buildPipelineTotalCostTooltip(
  cost: PipelineCostSummary,
): StructuredTooltip | null {
  if (cost.totalTokens <= 0) return null;
  const context = [
    `Input: ${formatPipelineCost(cost.totalInputCostUsd)}`,
    `Output: ${formatPipelineCost(cost.totalOutputCostUsd)}`,
    `Cache read: ${formatPipelineCost(cost.totalCacheReadCostUsd)}`,
    `Cache creation: ${formatPipelineCost(cost.totalCacheCreationCostUsd)}`,
  ];
  if (cost.anyModelUnknown) {
    context.push('One or more steps used a model with no price data; the estimate covers only priced usage.');
  }
  return {
    title: 'Pipeline total cost',
    body: buildTokenCostTooltip({
      costUsd: cost.totalCostUsd,
      priceKnown: !cost.anyModelUnknown,
      totalTokens: cost.totalTokens,
      context: context.join('\n'),
      unpricedRuns: cost.unpricedRuns ?? (cost.anyModelUnknown ? 1 : 0),
      pricingGaps: cost.pricingGaps,
    }),
  };
}
