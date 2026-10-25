/**
 * Filter state of the token analysis view and its `ta=` hash encoding
 * (AGT-3267). Kept apart from the aggregations so entry points that only
 * link into the view (board popover, card detail) stay small.
 */
import type { TokenAnalysisCard } from './models/token-analysis.model';
import { ANALYSIS_HASH_KEY } from './token-analysis-navigation.util';

export { ANALYSIS_HASH_KEY };

export const ANALYSIS_DAY_OPTIONS = [1, 7, 14, 30, 90] as const;
export const ANALYSIS_ROUND_OPTIONS = [2, 3, 5] as const;
export const DEFAULT_ANALYSIS_DAYS = 30;

export interface TokenAnalysisFilters {
  days: number;
  project: string | null;
  lane: string | null;
  type: string | null;
  tag: string | null;
  model: string | null;
  level: string | null;
  cli: string | null;
  host: string | null;
  kind: string | null;
  outcome: string | null;
  minRounds: number | null;
  /** Drill-down card as `project/jobId`. */
  card: string | null;
}

export const EMPTY_FILTERS: TokenAnalysisFilters = {
  days: DEFAULT_ANALYSIS_DAYS,
  project: null, lane: null, type: null, tag: null,
  model: null, level: null, cli: null, host: null, kind: null, outcome: null,
  minRounds: null, card: null,
};

const STRING_KEYS = ['project', 'lane', 'type', 'tag', 'model', 'level', 'cli', 'host', 'kind', 'outcome', 'card'] as const;
type StringFilterKey = (typeof STRING_KEYS)[number];


/** Serialize filters for the `ta=` hash segment; defaults are omitted. Returns null when nothing is set. */
export function encodeAnalysisFilters(filters: TokenAnalysisFilters): string | null {
  const params = new URLSearchParams();
  if (filters.days !== DEFAULT_ANALYSIS_DAYS) params.set('days', String(filters.days));
  for (const key of STRING_KEYS) {
    const value = filters[key];
    if (value) params.set(key, value);
  }
  if (filters.minRounds) params.set('rounds', String(filters.minRounds));
  const text = params.toString();
  return text ? encodeURIComponent(text) : null;
}

/** Inverse of {@link encodeAnalysisFilters}; unknown or invalid values fall back to defaults. */
export function decodeAnalysisFilters(encoded: string | null | undefined): TokenAnalysisFilters {
  if (!encoded) return { ...EMPTY_FILTERS };
  let params: URLSearchParams;
  try {
    params = new URLSearchParams(decodeURIComponent(encoded));
  } catch {
    return { ...EMPTY_FILTERS };
  }
  const result: TokenAnalysisFilters = { ...EMPTY_FILTERS };
  const days = Number(params.get('days'));
  if ((ANALYSIS_DAY_OPTIONS as readonly number[]).includes(days)) result.days = days;
  for (const key of STRING_KEYS) {
    const value = params.get(key);
    if (value && value.trim()) (result as Record<StringFilterKey, string | null>)[key] = value.trim();
  }
  const rounds = Number(params.get('rounds'));
  if (Number.isInteger(rounds) && rounds > 1) result.minRounds = rounds;
  return result;
}

export function cardId(card: Pick<TokenAnalysisCard, 'project' | 'jobId'>): string {
  return `${card.project}/${card.jobId}`;
}

/** Split a `project/jobId` drill-down id; the project name may not contain `/`. */
export function parseCardId(id: string | null): { project: string; jobId: string } | null {
  if (!id) return null;
  const at = id.indexOf('/');
  if (at <= 0 || at === id.length - 1) return null;
  return { project: id.slice(0, at), jobId: id.slice(at + 1) };
}
