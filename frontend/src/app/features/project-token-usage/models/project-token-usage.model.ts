/**
 * Cycle 9 project-token-usage feature models. Lifted out of
 * `models/job.model.ts` per ADR-0034. Re-exported from the legacy file.
 *
 * Slice 8 of the quality-system mockup (docs/mockups/quality-system/).
 * Mirrors backend `ProjectTokenUsageSummary`, `ProjectTokenHeatmap`,
 * `ProjectExpensiveJobsResponse`, `ProjectJobTokenDetail`. The category
 * split (`job` / `supporting` / `orchestrator`) follows taxonomy.md.
 */

export type ProjectTokenCategory = 'job' | 'supporting' | 'orchestrator';

export interface ProjectTokenDataFreshness {
  status: 'complete' | 'partial' | 'unavailable';
  asOf: string | null;
  warning: string | null;
  sources: string[];
}

export interface ProjectTokenUsageSummary {
  project: string;
  hasData: boolean;
  lifetimeTotalTokens: number;
  lifetimeJobTokens: number;
  lifetimeSupportingTokens: number;
  lifetimeOrchestratorTokens: number;
  lifetimeCalls: number;
  last24hTotalTokens: number;
  last24hJobTokens: number;
  last24hSupportingTokens: number;
  last24hOrchestratorTokens: number;
  last24hCalls: number;
  last7dTotalTokens: number;
  last7dJobTokens: number;
  last7dSupportingTokens: number;
  last7dOrchestratorTokens: number;
  last7dCalls: number;
  last7dBetterCandidateTokens?: number;
  last7dBetterCandidateCostUsd?: number;
  last7dBetterCandidateCalls?: number;
  allBetterCandidateModelsPriced?: boolean;
  firstActivity: string | null;
  lastActivity: string | null;
  fetchedAt: string;
  freshness?: ProjectTokenDataFreshness;
  disclaimer: string;
}

export interface ProjectTokenHeatmapCell {
  day: string;
  total: number;
}

export interface ProjectTokenHeatmapJob {
  jobId: string;
  title: string;
  state: string | null;
  category: ProjectTokenCategory;
  total: number;
  calls: number;
  lastActivity: string | null;
  cells: ProjectTokenHeatmapCell[];
}

export interface ProjectTokenHeatmap {
  project: string;
  days: string[];
  jobs: ProjectTokenHeatmapJob[];
  hasData: boolean;
  fetchedAt: string;
  freshness?: ProjectTokenDataFreshness;
}

export interface ProjectExpensiveJob {
  jobId: string;
  title: string;
  state: string | null;
  category: ProjectTokenCategory;
  totalTokens: number;
  calls: number;
  lastActivity: string | null;
  lastModel: string | null;
}

export interface ProjectExpensiveJobsResponse {
  project: string;
  jobs: ProjectExpensiveJob[];
}

export interface ProjectJobTokenRun {
  index: number;
  ts: string;
  model: string | null;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;
  total: number;
  deltaVsPrev: number | null;
  topic: string | null;
  summary: string | null;
}

export interface ProjectJobTokenDetail {
  project: string;
  jobId: string;
  title: string;
  state: string | null;
  category: ProjectTokenCategory;
  totalTokens: number;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;
  calls: number;
  firstActivity: string | null;
  lastActivity: string | null;
  lastModel: string | null;
  runs: ProjectJobTokenRun[];
  fetchedAt: string;
}

/**
 * Per-step-kind pipeline cost over time. Mirrors backend
 * `ProjectPipelineCostTimeline`: the "how it develops over time" view that
 * folds every task's pipeline-execution.json into a per-day series per
 * step kind, priced through the single TokenPricing table.
 */
export type PipelineStepKindKey = 'core' | 'aspect' | 'tool' | 'analysis' | 'orchestrator' | 'drift' | 'module';

export interface PipelinePricingGap {
  modelId: string;
  reason: string;
  affectedRuns: number;
}

export interface PipelineKindDayCell {
  day: string;
  totalTokens: number;
  costUsd: number;
  unpricedRuns?: number;
  pricingGaps?: PipelinePricingGap[];
}

export type PipelineDayCostCell = PipelineKindDayCell;

export interface PipelineKindSeries {
  kind: PipelineStepKindKey;
  totalTokens: number;
  totalCostUsd: number;
  anyModelUnknown: boolean;
  unpricedRuns?: number;
  pricingGaps?: PipelinePricingGap[];
  cells: PipelineKindDayCell[];
}

/**
 * One pipeline step's token + cost rollup over the whole window, folded
 * across every task run in the project. Keyed by `stepId` so the Pipeline
 * configuration page can show, per step, how many tokens it spent. Mirrors
 * backend `PipelineStepCostSeries`.
 */
export interface PipelineStepCostSeries {
  stepId: string;
  kind: PipelineStepKindKey;
  totalTokens: number;
  totalCostUsd: number;
  anyModelUnknown: boolean;
  unpricedRuns?: number;
  pricingGaps?: PipelinePricingGap[];
}

export interface ProjectPipelineCostTimeline {
  project: string;
  days: string[];
  dayCosts?: PipelineDayCostCell[];
  windowDays: number;
  kinds: PipelineKindSeries[];
  steps: PipelineStepCostSeries[];
  totalTokens: number;
  totalCostUsd: number;
  anyModelUnknown: boolean;
  unpricedRuns?: number;
  pricingGaps?: PipelinePricingGap[];
  taskCount: number;
  hasData: boolean;
  fetchedAt: string;
  freshness?: ProjectTokenDataFreshness;
}
