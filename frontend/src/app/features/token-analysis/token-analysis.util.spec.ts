import { describe, expect, it } from 'vitest';
import type { TokenAnalysisCard, TokenAnalysisSlice } from './models/token-analysis.model';
import {
  activeKinds,
  cardId,
  decodeAnalysisFilters,
  distribution,
  dominant,
  EMPTY_FILTERS,
  encodeAnalysisFilters,
  filterCards,
  filterOptions,
  filterRemainders,
  kindColor,
  outcomeGroup,
  parseCardId,
  quotaLines,
  scatterPoints,
  sliceTotal,
  stableIdentities,
  timeChart,
  tokenMix,
  topCardStacks,
} from './token-analysis.util';

function slice(partial: Partial<TokenAnalysisSlice>): TokenAnalysisSlice {
  return {
    kind: 'coding', model: 'GPT-5.6 Sol', level: 'high', cli: 'codex', host: 'local', day: '2026-10-19',
    input: 100, output: 10, cacheRead: 0, cacheWrite: 0, reasoning: null, costUsd: 0.01, priced: true, calls: 1,
    ...partial,
  };
}

function card(jobId: string, slices: TokenAnalysisSlice[], partial: Partial<TokenAnalysisCard> = {}): TokenAnalysisCard {
  return {
    project: 'Studio', jobId, key: jobId.toUpperCase(), title: `Card ${jobId}`, state: '5-human-review',
    taskType: 'feature', tags: ['tokens'], outcome: 'delivered', rounds: 1, inFlight: false,
    firstActivity: null, lastActivity: null,
    totalTokens: slices.reduce((sum, s) => sum + sliceTotal(s), 0),
    costUsd: slices.reduce((sum, s) => sum + s.costUsd, 0), costComplete: true, slices,
    ...partial,
  };
}

const cards: TokenAnalysisCard[] = [
  card('a', [
    slice({ kind: 'coding', input: 1000, output: 200, cacheRead: 3000, reasoning: 50 }),
    slice({ kind: 'review-aspect', model: 'Claude Sonnet 5', level: 'medium', cli: 'claude', input: 300, output: 30 }),
  ], { rounds: 3, outcome: 'integrated' }),
  card('b', [
    slice({ kind: 'fix-round', day: '2026-10-20', input: 500, output: 50 }),
    slice({ kind: 'chat', day: '2026-10-20', model: 'Claude Sonnet 5', level: 'low', cli: 'claude', host: 'runner-1', input: 40, output: 4, priced: false, costUsd: 0 }),
  ], { rounds: 2, outcome: 'blocked', inFlight: true, state: '3-progress', tags: ['ui'] }),
];

describe('token analysis filters', () => {
  it('round-trips filter state through the hash segment and omits defaults', () => {
    expect(encodeAnalysisFilters(EMPTY_FILTERS)).toBeNull();
    const filters = { ...EMPTY_FILTERS, days: 7, model: 'GPT-5.6 Sol', kind: 'fix-round', minRounds: 2, card: 'Studio/a' };
    const encoded = encodeAnalysisFilters(filters)!;
    expect(encoded).not.toContain('&');
    expect(decodeAnalysisFilters(encoded)).toEqual(filters);
  });

  it('falls back to defaults on invalid or unknown values', () => {
    const decoded = decodeAnalysisFilters(encodeURIComponent('days=13&rounds=x&bogus=1&cli=%20'));
    expect(decoded).toEqual(EMPTY_FILTERS);
    expect(decodeAnalysisFilters('%E0%A4%A')).toEqual(EMPTY_FILTERS);
  });

  it('parses drill-down ids', () => {
    expect(parseCardId(cardId(cards[0]))).toEqual({ project: 'Studio', jobId: 'a' });
    expect(parseCardId('nope')).toBeNull();
    expect(parseCardId(null)).toBeNull();
  });

  it('keeps the card total equal to the sum of the visible slices', () => {
    const byModel = filterCards(cards, { ...EMPTY_FILTERS, model: 'Claude Sonnet 5' });
    expect(byModel.map(c => c.jobId)).toEqual(['a', 'b']);
    expect(byModel[0].totalTokens).toBe(330);
    expect(byModel[1].totalTokens).toBe(44);
    expect(byModel[1].costComplete).toBe(false);
    for (const c of byModel) expect(c.totalTokens).toBe(c.slices.reduce((sum, s) => sum + sliceTotal(s), 0));
  });

  it('applies card filters and drops cards without remaining tokens', () => {
    expect(filterCards(cards, { ...EMPTY_FILTERS, minRounds: 3 }).map(c => c.jobId)).toEqual(['a']);
    expect(filterCards(cards, { ...EMPTY_FILTERS, tag: 'ui' }).map(c => c.jobId)).toEqual(['b']);
    expect(filterCards(cards, { ...EMPTY_FILTERS, outcome: 'blocked', kind: 'coding' })).toEqual([]);
    expect(filterCards(cards, { ...EMPTY_FILTERS, host: 'runner-1' })[0].totalTokens).toBe(44);
    expect(filterCards(cards, EMPTY_FILTERS).map(c => c.jobId)).toEqual(['a', 'b']);
  });

  it('filters project usage outside cards by slice filters and hides it for card-only filters', () => {
    const rest = [{ project: 'Studio', totalTokens: 330, costUsd: 0.02, slices: [
      slice({ kind: 'chat', input: 100, output: 10 }), slice({ kind: 'orchestrator-decision', cli: 'claude', input: 200, output: 20 }),
    ] }];
    expect(filterRemainders(rest, { ...EMPTY_FILTERS, cli: 'claude' })[0].totalTokens).toBe(220);
    expect(filterRemainders(rest, { ...EMPTY_FILTERS, outcome: 'blocked' })).toEqual([]);
    expect(filterRemainders(rest, { ...EMPTY_FILTERS, project: 'Other' })).toEqual([]);
  });

  it('lists the options of every dimension with kinds in canonical order', () => {
    const options = filterOptions(cards);
    expect(options.kinds).toEqual(['coding', 'fix-round', 'review-aspect', 'chat']);
    expect(options.clis).toEqual(['claude', 'codex']);
    expect(options.tags).toEqual(['tokens', 'ui']);
    expect(options.outcomes).toEqual(['blocked', 'integrated']);
  });
});

describe('token analysis aggregations', () => {
  it('stacks the usage kinds of the top cards with fixed kind colours', () => {
    const rows = topCardStacks(filterCards(cards, EMPTY_FILTERS), 1);
    expect(rows).toHaveLength(1);
    expect(rows[0].segments.map(s => [s.key, s.value])).toEqual([['coding', 4200], ['review-aspect', 330]]);
    expect(rows[0].segments.reduce((sum, s) => sum + s.value, 0)).toBe(rows[0].total);
    expect(kindColor('coding')).toBe('var(--studio-series-1)');
    expect(kindColor('other')).toBe('var(--studio-series-other)');
    expect(activeKinds(cards)).toEqual(['coding', 'fix-round', 'review-aspect', 'chat']);
  });

  it('names the dominant model of a card and counts the others', () => {
    expect(dominant(cards[0], 'model')).toEqual({ value: 'GPT-5.6 Sol', extra: 1 });
    expect(dominant(cards[1], 'host')).toEqual({ value: 'local', extra: 1 });
  });

  it('buckets tokens per day by model and level and marks incomplete days', () => {
    const chart = timeChart(cards, '2026-10-19T00:00:00Z', '2026-10-21T10:00:00Z');
    expect(chart.days).toEqual(['2026-10-19', '2026-10-20', '2026-10-21']);
    expect(chart.totals).toEqual([4530, 594, 0]);
    expect(chart.incomplete).toEqual([false, true, true]);
    const sol = chart.series.find(s => s.key === 'GPT-5.6 Sol · high')!;
    expect(sol.values).toEqual([4200, 550, 0]);
  });

  it('keeps series colours stable when a filter removes an identity', () => {
    const identities = stableIdentities(cards);
    const full = timeChart(cards, '2026-10-19T00:00:00Z', '2026-10-21T00:00:00Z', identities);
    const filtered = timeChart(filterCards(cards, { ...EMPTY_FILTERS, kind: 'chat' }),
      '2026-10-19T00:00:00Z', '2026-10-21T00:00:00Z', identities);
    const colourOf = (chart: typeof full, key: string) => chart.series.find(s => s.key === key)?.color;
    expect(colourOf(filtered, 'Claude Sonnet 5 · low')).toBe(colourOf(full, 'Claude Sonnet 5 · low'));
  });

  it('folds identities beyond the stable list into Other', () => {
    const many = Array.from({ length: 10 }, (_, i) => card(`c${i}`, [slice({ model: `M${i}`, input: 100 + i })]));
    const chart = timeChart(many, '2026-10-19T00:00:00Z', '2026-10-20T00:00:00Z');
    expect(chart.series).toHaveLength(8);
    expect(chart.series[7].label).toBe('Other (3)');
    expect(chart.totals[0]).toBe(many.reduce((sum, c) => sum + c.totalTokens, 0));
  });

  it('places cards by rounds and tokens in three outcome groups', () => {
    expect(scatterPoints(cards).map(p => [p.rounds, p.tokens, p.group])).toEqual([[3, 4530, 'done'], [2, 594, 'stuck']]);
    expect(outcomeGroup('in-progress')).toBe('open');
    expect(outcomeGroup('escalated')).toBe('stuck');
  });

  it('distributes tokens per level with per-card median and p90', () => {
    const many = [100, 200, 300, 400, 1000].map((n, i) => card(`d${i}`, [slice({ input: n, output: 0 })]));
    const [high] = distribution(many, 'level');
    expect(high).toMatchObject({ label: 'high', tokens: 2000, cards: 5, medianPerCard: 300, p90PerCard: 760 });
    expect(distribution(cards, 'model').map(r => r.label)).toEqual(['GPT-5.6 Sol', 'Claude Sonnet 5']);
  });

  it('splits the token mix so the five parts sum to the total', () => {
    const mix = tokenMix(cards.flatMap(c => c.slices));
    expect(mix).toMatchObject({ cachedInput: 3000, uncachedInput: 1840, output: 234, reasoning: 50 });
    expect(mix.cachedInput + mix.uncachedInput + mix.cacheWrite + mix.output + mix.reasoning).toBe(mix.total);
    expect(mix.reasoningReportedOutput).toBe(200);
  });

  it('places quota readings on the day axis', () => {
    const lines = quotaLines([
      { cli: 'codex', window: 'Weekly', points: [{ at: '2026-10-19T12:00:00Z', usedPct: 10 }, { at: '2026-10-25T00:00:00Z', usedPct: 90 }], segments: [] },
      { cli: 'claude', window: '5h', points: [{ at: '2026-10-20T00:00:00Z', usedPct: 50 }], segments: [] },
    ], ['2026-10-19', '2026-10-20'], null);
    expect(lines.map(l => l.label)).toEqual(['claude 5h', 'codex Weekly']);
    expect(lines[1].points).toEqual([{ x: 0.5, y: 10, at: '2026-10-19T12:00:00Z' }]);
    expect(quotaLines([], [], 'codex')).toEqual([]);
  });
});

describe('token analysis entry links', () => {
  it('opens the analysis route with a drill-down the view decodes, keeping foreign segments', async () => {
    const { tokenAnalysisCardHash } = await import('./token-analysis-navigation.util');
    const hash = tokenAnalysisCardHash('#/board&filters=abc', 'My Project', 'job-1');
    expect(hash.startsWith('#/workspace/settings/tokens/analysis&ta=')).toBe(true);
    expect(hash).toContain('filters=abc');
    const ta = hash.split('&').find(s => s.startsWith('ta='))!.slice(3);
    expect(decodeAnalysisFilters(ta)).toEqual({ ...EMPTY_FILTERS, card: 'My Project/job-1' });
    expect(ta).toBe(encodeAnalysisFilters({ ...EMPTY_FILTERS, card: 'My Project/job-1' }));
  });
});
