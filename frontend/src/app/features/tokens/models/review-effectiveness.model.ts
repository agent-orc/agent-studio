/** Wire shape of `GET /api/review-effectiveness` (AGT-3269). */
export type ReviewLevel = 'aspect' | 'grade' | 'deep';

export interface ReviewFindingCounts {
  blocker: number;
  major: number;
  minor: number;
  nit: number;
  unclassified: number;
}

/** One review route. Rates and cost figures are null when unknown, never zero. */
export interface ReviewEffectivenessRow {
  level: ReviewLevel;
  model: string;
  reasoningLevel: string;
  reviews: number;
  approvals: number;
  blocks: number;
  blockerRate: number | null;
  missedApprovals: number;
  missRate: number | null;
  missesBySource: Record<string, number>;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;
  pricedReviews: number;
  totalCostUsd: number | null;
  costPerReviewUsd: number | null;
  findings: ReviewFindingCounts;
  realFindings: number;
  costPerRealFindingUsd: number | null;
  averageDurationMs: number | null;
}

export interface ReviewEffectivenessCoverage {
  reviews: number;
  pricedReviews: number;
  reviewsWithTokens: number;
  reviewsWithModel: number;
  evidence: number;
  misses: number;
}

export interface ReviewEffectivenessSummary {
  generatedAtUtc: string;
  sinceUtc: string | null;
  rows: ReviewEffectivenessRow[];
  coverage: ReviewEffectivenessCoverage;
}
