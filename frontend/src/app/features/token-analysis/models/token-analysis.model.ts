/**
 * Wire shapes of `GET /api/workspace/tokens/analysis` and
 * `GET /api/projects/{p}/token-usage/analysis/cards/{jobId}` (AGT-3267).
 * Mirrors `backend/Features/Tokens/Analysis/TokenAnalysisModels.cs`.
 */

/** Usage kinds; every token of a card lands in exactly one. */
export const TOKEN_USAGE_KINDS = [
  'coding',
  'fix-round',
  'review-aspect',
  'review-grade',
  'deep-review',
  'gate',
  'orchestrator-decision',
  'chat',
  'enrichment',
  'other',
] as const;
export type TokenUsageKind = (typeof TOKEN_USAGE_KINDS)[number];

export const CARD_OUTCOMES = [
  'integrated', 'completed', 'delivered', 'escalated', 'blocked', 'in-progress', 'open',
] as const;
export type CardOutcome = (typeof CARD_OUTCOMES)[number];

/** One cell of a card's usage cube (kind x model x level x CLI x host x UTC day). */
export interface TokenAnalysisSlice {
  kind: TokenUsageKind | string;
  model: string;
  level: string;
  cli: string;
  host: string;
  day: string;
  input: number;
  output: number;
  cacheRead: number;
  cacheWrite: number;
  /** Reported reasoning subset of `output`; null when not reported. */
  reasoning: number | null;
  costUsd: number;
  priced: boolean;
  calls: number;
}

export interface TokenAnalysisCard {
  project: string;
  jobId: string;
  key: string | null;
  title: string;
  state: string | null;
  taskType: string;
  tags: string[];
  outcome: CardOutcome | string;
  rounds: number;
  inFlight: boolean;
  firstActivity: string | null;
  lastActivity: string | null;
  totalTokens: number;
  costUsd: number;
  costComplete: boolean;
  slices: TokenAnalysisSlice[];
}

export interface TokenAnalysisProjectRemainder {
  project: string;
  totalTokens: number;
  costUsd: number;
  slices: TokenAnalysisSlice[];
}

export interface TokenAnalysisQuotaPoint {
  at: string;
  usedPct: number;
}

export interface TokenAnalysisQuotaSegment {
  from: string;
  to: string;
  startPct: number;
  endPct: number;
  tokens: number;
  uncachedTokens: number;
  tokensPerPct: number | null;
  uncachedTokensPerPct: number | null;
  open: boolean;
}

export interface TokenAnalysisQuotaWindow {
  cli: string;
  window: string;
  points: TokenAnalysisQuotaPoint[];
  segments: TokenAnalysisQuotaSegment[];
}

export interface TokenAnalysisFreshness {
  status: 'complete' | 'partial' | 'unavailable' | string;
  asOf?: string | null;
  warning?: string | null;
  sources?: string[];
}

export interface TokenAnalysisResponse {
  generatedAt: string;
  from: string;
  to: string;
  days: number;
  projects: string[];
  cards: TokenAnalysisCard[];
  remainders: TokenAnalysisProjectRemainder[];
  quota: TokenAnalysisQuotaWindow[];
  freshness: TokenAnalysisFreshness;
  disclaimer: string;
}

export interface TokenAnalysisEvent {
  at: string;
  endAt: string | null;
  kind: TokenUsageKind | string;
  label: string;
  attempt: number;
  model: string;
  level: string;
  cli: string;
  host: string;
  input: number;
  output: number;
  cacheRead: number;
  cacheWrite: number;
  reasoning: number | null;
  total: number;
  costUsd: number;
  priced: boolean;
  durationMs: number | null;
  result: string | null;
  calls: number;
  source: string;
}

export interface TokenAnalysisCardDetail {
  card: TokenAnalysisCard;
  events: TokenAnalysisEvent[];
  freshness: TokenAnalysisFreshness;
  disclaimer: string;
}
