export interface ReviewMetric {
  median: number | null;
  p90: number | null;
  samples: number;
  last7d: (number | null)[];
  last30d: (number | null)[];
}

export interface ReviewUsageRow {
  project: string;
  jobId: string;
  attempt: string;
  step: string;
  at: string;
  model: string;
  level: string;
  host: string;
  tokens: number;
  usd: number | null;
  cacheReadTokens?: number;
  cacheWriteTokens?: number;
}

export interface ReviewStatistics {
  deliveryTokens: ReviewMetric;
  deliveryUsd: ReviewMetric;
  /** Prompt-cache tokens read and written per delivery review (AGT-3971). */
  deliveryCacheReadTokens?: ReviewMetric;
  deliveryCacheWriteTokens?: ReviewMetric;
  roundsUntilAcceptance: ReviewMetric;
  tokensPerAcceptedCard: ReviewMetric;
  usdPerAcceptedCard: ReviewMetric;
  reviewSharePercent: ReviewMetric;
  byStepTokens: Record<string, ReviewMetric>;
  byStepUsd: Record<string, ReviewMetric>;
  byModelLevelTokens: Record<string, ReviewMetric>;
  byModelLevelUsd: Record<string, ReviewMetric>;
  acceptedCards: string[];
  reviews: ReviewUsageRow[];
  warning: string | null;
}
