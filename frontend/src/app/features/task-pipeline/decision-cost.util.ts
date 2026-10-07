/**
 * "What did deciding cost" wording shared by the task Overview and the
 * project usage view (AGT-3015). Deciding = orchestrator-kind steps; agent
 * runs = the core run; other = reviews, drift, analysis and tools. An unpriced
 * share is stated next to the priced amount, never folded into a zero.
 */
import { formatTokenCostUsd } from '../tokens/token-cost-tooltip.util';
import type { DecisionCostBucket, DecisionCostRollup } from './models/task-pipeline.model';

export interface DecisionCostSummaryVm {
  decidingText: string;
  agentRunsText: string;
  otherText: string;
  decidingRuns: number;
  agentRuns: number;
  /** "N tokens on M runs without a price" or null when everything is priced. */
  unpricedText: string | null;
  /** Deciding share of the priced total, e.g. "4.2%"; null when nothing was priced. */
  decidingShare: string | null;
}

function bucketText(bucket: DecisionCostBucket): string {
  if (bucket.tokens <= 0) return '$0';
  if (bucket.pricedCostUsd <= 0 && bucket.unpricedTokens > 0) return 'no price data';
  const priced = formatTokenCostUsd(bucket.pricedCostUsd);
  return bucket.unpricedTokens > 0 ? `${priced}+` : priced;
}

/** Card- and project-level "what did deciding cost" wording. */
export function summarizeDecisionCost(rollup: DecisionCostRollup | null | undefined): DecisionCostSummaryVm | null {
  if (rollup == null) return null;
  const { deciding, agentRuns, other } = rollup;
  if (deciding.runs + agentRuns.runs + other.runs === 0 && deciding.tokens + agentRuns.tokens + other.tokens === 0) {
    return null;
  }
  const unpricedTokens = deciding.unpricedTokens + agentRuns.unpricedTokens + other.unpricedTokens;
  const unpricedRuns = deciding.unpricedRuns + agentRuns.unpricedRuns + other.unpricedRuns;
  const pricedTotal = deciding.pricedCostUsd + agentRuns.pricedCostUsd + other.pricedCostUsd;
  return {
    decidingText: bucketText(deciding),
    agentRunsText: bucketText(agentRuns),
    otherText: bucketText(other),
    decidingRuns: deciding.runs,
    agentRuns: agentRuns.runs,
    unpricedText: unpricedTokens > 0
      ? `${unpricedTokens.toLocaleString('en-US')} tokens on ${unpricedRuns} run${unpricedRuns === 1 ? '' : 's'} without a price`
      : null,
    decidingShare: pricedTotal > 0 ? `${((deciding.pricedCostUsd / pricedTotal) * 100).toFixed(1)}%` : null,
  };
}
