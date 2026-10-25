import type {
  TokenAnalysisCard,
  TokenAnalysisEvent,
  TokenAnalysisProjectRemainder,
  TokenAnalysisQuotaWindow,
  TokenAnalysisSlice,
} from './models/token-analysis.model';
import { TOKEN_USAGE_KINDS } from './models/token-analysis.model';
import type { TokenAnalysisFilters } from './token-analysis-filters.util';
import { cardId } from './token-analysis-filters.util';

/**
 * Pure aggregations behind the card-centric token analysis (AGT-3267).
 *
 * The backend ships each card as a usage cube of slices. Every filter here
 * keeps or drops whole slices, and every total is re-summed from the slices
 * that survive, so a visible aggregate always equals the sum of its visible
 * children (style-guide hard rule R3).
 */

export const DEFAULT_TOP_N = 12;
export const SERIES_SLOTS = 8;

export {
  ANALYSIS_DAY_OPTIONS, ANALYSIS_HASH_KEY, ANALYSIS_ROUND_OPTIONS, DEFAULT_ANALYSIS_DAYS, EMPTY_FILTERS,
  cardId, decodeAnalysisFilters, encodeAnalysisFilters, parseCardId,
} from './token-analysis-filters.util';
export type { TokenAnalysisFilters } from './token-analysis-filters.util';
export function sliceTotal(slice: Pick<TokenAnalysisSlice, 'input' | 'output' | 'cacheRead' | 'cacheWrite'>): number {
  return slice.input + slice.output + slice.cacheRead + slice.cacheWrite;
}

function sliceMatches(slice: TokenAnalysisSlice, f: TokenAnalysisFilters): boolean {
  return (!f.model || slice.model === f.model)
    && (!f.level || slice.level === f.level)
    && (!f.cli || slice.cli === f.cli)
    && (!f.host || slice.host === f.host)
    && (!f.kind || slice.kind === f.kind);
}

function cardMatches(card: TokenAnalysisCard, f: TokenAnalysisFilters): boolean {
  return (!f.project || card.project === f.project)
    && (!f.lane || card.state === f.lane)
    && (!f.type || card.taskType === f.type)
    && (!f.tag || card.tags.includes(f.tag))
    && (!f.outcome || card.outcome === f.outcome)
    && (!f.minRounds || card.rounds >= f.minRounds);
}

/**
 * Cards that pass the card filters, each reduced to the slices that pass
 * the slice filters, with totals re-summed. A card left with no tokens is
 * dropped. Sorted by tokens, most expensive first.
 */
export function filterCards(cards: readonly TokenAnalysisCard[], f: TokenAnalysisFilters): TokenAnalysisCard[] {
  const result: TokenAnalysisCard[] = [];
  for (const card of cards) {
    if (!cardMatches(card, f)) continue;
    const slices = card.slices.filter(slice => sliceMatches(slice, f));
    const totalTokens = slices.reduce((sum, slice) => sum + sliceTotal(slice), 0);
    if (totalTokens <= 0) continue;
    result.push({
      ...card,
      slices,
      totalTokens,
      costUsd: slices.reduce((sum, slice) => sum + slice.costUsd, 0),
      costComplete: slices.every(slice => slice.priced),
    });
  }
  return result.sort((a, b) => b.totalTokens - a.totalTokens || a.jobId.localeCompare(b.jobId));
}

/**
 * Project usage outside cards under the same filters. Card-only filters
 * (lane, type, tag, outcome, rounds) exclude it, because it is no card.
 */
export function filterRemainders(
  remainders: readonly TokenAnalysisProjectRemainder[], f: TokenAnalysisFilters,
): TokenAnalysisProjectRemainder[] {
  if (f.lane || f.type || f.tag || f.outcome || f.minRounds) return [];
  return remainders
    .filter(remainder => !f.project || remainder.project === f.project)
    .map(remainder => {
      const slices = remainder.slices.filter(slice => sliceMatches(slice, f));
      return {
        ...remainder, slices,
        totalTokens: slices.reduce((sum, slice) => sum + sliceTotal(slice), 0),
        costUsd: slices.reduce((sum, slice) => sum + slice.costUsd, 0),
      };
    })
    .filter(remainder => remainder.totalTokens > 0);
}

export interface FilterOptions {
  projects: string[]; lanes: string[]; types: string[]; tags: string[];
  models: string[]; levels: string[]; clis: string[]; hosts: string[]; kinds: string[]; outcomes: string[];
}

/** Distinct values per dimension over the unfiltered cards, sorted. */
export function filterOptions(cards: readonly TokenAnalysisCard[]): FilterOptions {
  const sets = {
    projects: new Set<string>(), lanes: new Set<string>(), types: new Set<string>(), tags: new Set<string>(),
    models: new Set<string>(), levels: new Set<string>(), clis: new Set<string>(), hosts: new Set<string>(),
    kinds: new Set<string>(), outcomes: new Set<string>(),
  };
  for (const card of cards) {
    sets.projects.add(card.project);
    if (card.state) sets.lanes.add(card.state);
    sets.types.add(card.taskType);
    card.tags.forEach(tag => sets.tags.add(tag));
    sets.outcomes.add(card.outcome);
    for (const slice of card.slices) {
      sets.models.add(slice.model);
      sets.levels.add(slice.level);
      sets.clis.add(slice.cli);
      sets.hosts.add(slice.host);
      sets.kinds.add(slice.kind);
    }
  }
  const sorted = (set: Set<string>) => [...set].sort((a, b) => a.localeCompare(b));
  return {
    projects: sorted(sets.projects), lanes: sorted(sets.lanes), types: sorted(sets.types), tags: sorted(sets.tags),
    models: sorted(sets.models), levels: sorted(sets.levels), clis: sorted(sets.clis), hosts: sorted(sets.hosts),
    kinds: TOKEN_USAGE_KINDS.filter(kind => sets.kinds.has(kind)),
    outcomes: sorted(sets.outcomes),
  };
}

export const KIND_LABELS: Record<string, string> = {
  'coding': 'Coding run',
  'fix-round': 'Fix round',
  'review-aspect': 'Review aspect',
  'review-grade': 'Review grade',
  'deep-review': 'Deep review',
  'gate': 'Gate',
  'orchestrator-decision': 'Decision',
  'chat': 'Chat',
  'enrichment': 'Enrichment',
  'other': 'Other',
};

export function kindLabel(kind: string): string {
  return KIND_LABELS[kind] ?? kind;
}

/** Fixed colour per usage kind: identity never follows rank. */
export function kindColor(kind: string): string {
  const index = TOKEN_USAGE_KINDS.indexOf(kind as (typeof TOKEN_USAGE_KINDS)[number]);
  return index >= 0 && index < SERIES_SLOTS ? `var(--studio-series-${index + 1})` : 'var(--studio-series-other)';
}

/** Colour of the n-th series in a fixed-order list; the ninth and later fold to Other. */
export function seriesColor(index: number): string {
  return index >= 0 && index < SERIES_SLOTS ? `var(--studio-series-${index + 1})` : 'var(--studio-series-other)';
}

export function kindTotals(card: Pick<TokenAnalysisCard, 'slices'>): Map<string, number> {
  const totals = new Map<string, number>();
  for (const slice of card.slices) totals.set(slice.kind, (totals.get(slice.kind) ?? 0) + sliceTotal(slice));
  return totals;
}

/** Kinds with any tokens across the cards, in canonical order. */
export function activeKinds(cards: readonly Pick<TokenAnalysisCard, 'slices'>[]): string[] {
  const seen = new Set<string>();
  for (const card of cards) for (const slice of card.slices) if (sliceTotal(slice) > 0) seen.add(slice.kind);
  return [...TOKEN_USAGE_KINDS.filter(kind => seen.has(kind)), ...[...seen].filter(kind =>
    !(TOKEN_USAGE_KINDS as readonly string[]).includes(kind)).sort()];
}

export interface DominantValue { value: string; extra: number; }

/** The value of `dimension` that spent the most tokens on the card, and how many others it has. */
export function dominant(card: Pick<TokenAnalysisCard, 'slices'>, dimension: 'model' | 'level' | 'cli' | 'host'): DominantValue {
  const totals = new Map<string, number>();
  for (const slice of card.slices) totals.set(slice[dimension], (totals.get(slice[dimension]) ?? 0) + sliceTotal(slice));
  let best = '';
  let bestTotal = -1;
  for (const [value, total] of totals) {
    if (total > bestTotal || (total === bestTotal && value < best)) { best = value; bestTotal = total; }
  }
  return { value: best || 'unknown', extra: Math.max(0, totals.size - 1) };
}

export interface StackSegment { key: string; label: string; value: number; color: string; }
export interface StackRow { id: string; label: string; title: string; total: number; segments: StackSegment[]; }

/** Stacked usage kinds of the top `n` cards. */
export function topCardStacks(cards: readonly TokenAnalysisCard[], n: number): StackRow[] {
  return cards.slice(0, n).map(card => {
    const totals = kindTotals(card);
    return {
      id: cardId(card),
      label: card.key ?? card.jobId,
      title: card.title,
      total: card.totalTokens,
      segments: TOKEN_USAGE_KINDS
        .filter(kind => (totals.get(kind) ?? 0) > 0)
        .map(kind => ({ key: kind, label: kindLabel(kind), value: totals.get(kind)!, color: kindColor(kind) })),
    };
  });
}

/** UTC days from `from` to `to`, inclusive, as `yyyy-MM-dd`. */
export function dayRange(fromIso: string, toIso: string): string[] {
  const days: string[] = [];
  const start = new Date(fromIso.slice(0, 10) + 'T00:00:00Z').getTime();
  const end = new Date(toIso).getTime();
  for (let at = start; at < end && days.length < 400; at += 86_400_000) {
    days.push(new Date(at).toISOString().slice(0, 10));
  }
  return days;
}

export interface TimeSeries { key: string; label: string; color: string; values: number[]; }
export interface TimeChart { days: string[]; series: TimeSeries[]; totals: number[]; incomplete: boolean[]; }

/** `model · level` identity of a slice in the time chart. */
export function modelLevelKey(slice: Pick<TokenAnalysisSlice, 'model' | 'level'>): string {
  return `${slice.model} · ${slice.level}`;
}

/**
 * The model and level identities that keep their own colour: the seven
 * largest over the unfiltered cards. Computed once per response so a filter
 * never repaints or reorders the survivors.
 */
export function stableIdentities(cards: readonly TokenAnalysisCard[], maxSeries = SERIES_SLOTS): string[] {
  const grand = new Map<string, number>();
  for (const card of cards) for (const slice of card.slices) {
    grand.set(modelLevelKey(slice), (grand.get(modelLevelKey(slice)) ?? 0) + sliceTotal(slice));
  }
  const ranked = [...grand.entries()].sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0])).map(([key]) => key);
  return ranked.length <= maxSeries ? ranked : ranked.slice(0, maxSeries - 1);
}

/**
 * Daily tokens stacked by model and reasoning level. Identities in
 * `identities` keep their slot colour; every other identity folds into
 * "Other". A day is incomplete when it is today (the window is still open)
 * or when an in-flight card spent tokens on it.
 */
export function timeChart(
  cards: readonly TokenAnalysisCard[], fromIso: string, toIso: string,
  identities: readonly string[] = stableIdentities(cards),
): TimeChart {
  const days = dayRange(fromIso, toIso);
  const index = new Map(days.map((day, i) => [day, i]));
  const perSeries = new Map<string, number[]>();
  const grand = new Map<string, number>();
  const incomplete = days.map(() => false);
  for (const card of cards) {
    for (const slice of card.slices) {
      const i = index.get(slice.day);
      if (i === undefined) continue;
      const key = modelLevelKey(slice);
      const values = perSeries.get(key) ?? days.map(() => 0);
      values[i] += sliceTotal(slice);
      perSeries.set(key, values);
      grand.set(key, (grand.get(key) ?? 0) + sliceTotal(slice));
      if (card.inFlight) incomplete[i] = true;
    }
  }
  if (days.length > 0 && days[days.length - 1] === toIso.slice(0, 10)) incomplete[days.length - 1] = true;
  const series: TimeSeries[] = identities
    .map((key, slot) => ({ key, label: key, color: seriesColor(slot), values: perSeries.get(key) }))
    .filter((s): s is TimeSeries => s.values !== undefined);
  const kept = new Set(identities);
  const folded = [...grand.keys()].filter(key => !kept.has(key)).sort();
  if (folded.length > 0) {
    const values = days.map((_, i) => folded.reduce((sum, key) => sum + perSeries.get(key)![i], 0));
    series.push({ key: 'other', label: `Other (${folded.length})`, color: 'var(--studio-series-other)', values });
  }
  const totals = days.map((_, i) => series.reduce((sum, s) => sum + s.values[i], 0));
  return { days, series, totals, incomplete };
}

export type OutcomeGroup = 'done' | 'stuck' | 'open';

/** Scatter colour groups: three slots stay distinguishable for every reader. */
export function outcomeGroup(outcome: string): OutcomeGroup {
  if (outcome === 'integrated' || outcome === 'completed' || outcome === 'delivered') return 'done';
  if (outcome === 'blocked' || outcome === 'escalated') return 'stuck';
  return 'open';
}

export const OUTCOME_GROUP_LABELS: Record<OutcomeGroup, string> = {
  done: 'Delivered, integrated or completed',
  stuck: 'Blocked or escalated',
  open: 'In progress or open',
};

export function outcomeGroupColor(group: OutcomeGroup): string {
  return group === 'done' ? seriesColor(0) : group === 'stuck' ? seriesColor(1) : seriesColor(2);
}

export interface ScatterPoint {
  id: string; label: string; title: string; rounds: number; tokens: number; outcome: string; group: OutcomeGroup;
}

export function scatterPoints(cards: readonly TokenAnalysisCard[]): ScatterPoint[] {
  return cards.map(card => ({
    id: cardId(card), label: card.key ?? card.jobId, title: card.title,
    rounds: card.rounds, tokens: card.totalTokens, outcome: card.outcome, group: outcomeGroup(card.outcome),
  }));
}

export interface DistributionRow {
  label: string; tokens: number; costUsd: number; cards: number; medianPerCard: number; p90PerCard: number;
}

function quantile(sorted: readonly number[], q: number): number {
  if (sorted.length === 0) return 0;
  const position = (sorted.length - 1) * q;
  const lower = Math.floor(position);
  const upper = Math.ceil(position);
  return Math.round(sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower));
}

/** Tokens per reasoning level or model, with the per-card median and 90th percentile. */
export function distribution(cards: readonly TokenAnalysisCard[], dimension: 'level' | 'model'): DistributionRow[] {
  const perGroup = new Map<string, { tokens: number; cost: number; perCard: Map<string, number> }>();
  for (const card of cards) {
    for (const slice of card.slices) {
      const label = slice[dimension];
      const group = perGroup.get(label) ?? { tokens: 0, cost: 0, perCard: new Map<string, number>() };
      group.tokens += sliceTotal(slice);
      group.cost += slice.costUsd;
      group.perCard.set(cardId(card), (group.perCard.get(cardId(card)) ?? 0) + sliceTotal(slice));
      perGroup.set(label, group);
    }
  }
  return [...perGroup.entries()]
    .map(([label, group]) => {
      const values = [...group.perCard.values()].sort((a, b) => a - b);
      return {
        label, tokens: group.tokens, costUsd: group.cost, cards: values.length,
        medianPerCard: quantile(values, 0.5), p90PerCard: quantile(values, 0.9),
      };
    })
    .sort((a, b) => b.tokens - a.tokens || a.label.localeCompare(b.label));
}

export interface TokenMix {
  cachedInput: number; uncachedInput: number; cacheWrite: number; output: number; reasoning: number;
  total: number;
  /** Output tokens whose slices reported a reasoning count. */
  reasoningReportedOutput: number;
}

/**
 * Share of cached input, uncached input, cache writes, output and reasoning.
 * Reasoning is carved out of output only where it was reported, so the five
 * parts sum to the total.
 */
export function tokenMix(slices: readonly Pick<TokenAnalysisSlice, 'input' | 'output' | 'cacheRead' | 'cacheWrite' | 'reasoning'>[]): TokenMix {
  const mix: TokenMix = { cachedInput: 0, uncachedInput: 0, cacheWrite: 0, output: 0, reasoning: 0, total: 0, reasoningReportedOutput: 0 };
  for (const slice of slices) {
    const reasoning = Math.min(slice.reasoning ?? 0, slice.output);
    mix.cachedInput += slice.cacheRead;
    mix.uncachedInput += slice.input;
    mix.cacheWrite += slice.cacheWrite;
    mix.reasoning += reasoning;
    mix.output += slice.output - reasoning;
    if (slice.reasoning !== null) mix.reasoningReportedOutput += slice.output;
  }
  mix.total = mix.cachedInput + mix.uncachedInput + mix.cacheWrite + mix.output + mix.reasoning;
  return mix;
}

export function allSlices(cards: readonly TokenAnalysisCard[]): TokenAnalysisSlice[] {
  return cards.flatMap(card => card.slices);
}

export interface QuotaLine { key: string; label: string; color: string; points: { x: number; y: number; at: string }[]; }

/** Quota window readings placed on the time chart's day axis (x in day units from the first day). */
export function quotaLines(windows: readonly TokenAnalysisQuotaWindow[], days: readonly string[], cli: string | null): QuotaLine[] {
  if (days.length === 0) return [];
  const start = new Date(days[0] + 'T00:00:00Z').getTime();
  const visible = windows.filter(window => !cli || window.cli === cli)
    .slice()
    .sort((a, b) => `${a.cli} ${a.window}`.localeCompare(`${b.cli} ${b.window}`));
  return visible.map((window, index) => ({
    key: `${window.cli}/${window.window}`,
    label: `${window.cli} ${window.window}`,
    color: seriesColor(index),
    points: window.points
      .map(point => ({ x: (new Date(point.at).getTime() - start) / 86_400_000, y: point.usedPct, at: point.at }))
      .filter(point => point.x >= 0 && point.x <= days.length),
  }));
}

/** Where a card spent its tokens: events with their share of the card total. */
export function eventShares(events: readonly TokenAnalysisEvent[]): { event: TokenAnalysisEvent; share: number }[] {
  const max = events.reduce((m, e) => Math.max(m, e.total), 0);
  return events.map(event => ({ event, share: max > 0 ? event.total / max : 0 }));
}

export function formatDuration(ms: number | null): string {
  if (ms === null || ms <= 0) return '';
  const seconds = Math.round(ms / 1000);
  if (seconds < 60) return `${seconds}s`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes}m ${seconds % 60}s`;
  return `${Math.floor(minutes / 60)}h ${minutes % 60}m`;
}
