import type { TokenAnalysisCardDetail, TokenAnalysisResponse, TokenAnalysisSlice } from '../models/token-analysis.model';

/** Shared spec fixtures for the token analysis components (AGT-3267). */
export function fixtureSlice(partial: Partial<TokenAnalysisSlice> = {}): TokenAnalysisSlice {
  return {
    kind: 'coding', model: 'GPT-5.6 Sol', level: 'high', cli: 'codex', host: 'local', day: '2026-10-20',
    input: 1000, output: 100, cacheRead: 500, cacheWrite: 0, reasoning: 40, costUsd: 0.5, priced: true, calls: 3,
    ...partial,
  };
}

export function fixtureResponse(): TokenAnalysisResponse {
  return {
    generatedAt: '2026-10-21T12:00:00Z', from: '2026-10-19T00:00:00Z', to: '2026-10-21T12:00:01Z', days: 3,
    projects: ['Studio'],
    cards: [
      {
        project: 'Studio', jobId: 'job-1', key: 'AGT-1', title: 'Expensive card', state: '5-human-review',
        taskType: 'feature', tags: ['tokens'], outcome: 'delivered', rounds: 3, inFlight: false,
        firstActivity: '2026-10-19T08:00:00Z', lastActivity: '2026-10-20T09:00:00Z',
        totalTokens: 2000, costUsd: 0.75, costComplete: true,
        slices: [
          fixtureSlice({ day: '2026-10-19' }),
          fixtureSlice({ kind: 'review-aspect', model: 'Claude Sonnet 5', level: 'medium', cli: 'claude',
            input: 300, output: 100, cacheRead: 0, reasoning: null, costUsd: 0.25 }),
        ],
      },
      {
        project: 'Studio', jobId: 'job-2', key: 'AGT-2', title: 'Running card', state: '3-progress',
        taskType: 'bug', tags: [], outcome: 'in-progress', rounds: 1, inFlight: true,
        firstActivity: '2026-10-21T08:00:00Z', lastActivity: '2026-10-21T09:00:00Z',
        totalTokens: 500, costUsd: 0.1, costComplete: true,
        slices: [fixtureSlice({ day: '2026-10-21', input: 400, output: 100, cacheRead: 0, reasoning: null, costUsd: 0.1 })],
      },
    ],
    remainders: [{ project: 'Studio', totalTokens: 100, costUsd: 0.01,
      slices: [fixtureSlice({ kind: 'chat', input: 90, output: 10, cacheRead: 0, reasoning: null, costUsd: 0.01 })] }],
    quota: [{
      cli: 'codex', window: 'Weekly',
      points: [{ at: '2026-10-19T06:00:00Z', usedPct: 10 }, { at: '2026-10-20T06:00:00Z', usedPct: 40 }],
      segments: [{ from: '2026-10-19T06:00:00Z', to: '2026-10-20T06:00:00Z', startPct: 10, endPct: 40,
        tokens: 3_000_000, uncachedTokens: 300_000, tokensPerPct: 100_000, uncachedTokensPerPct: 10_000, open: true }],
    }],
    freshness: { status: 'complete', sources: ['task-token-receipts'] },
    disclaimer: 'Theoretical cost at list prices.',
  };
}

export function fixtureDetail(): TokenAnalysisCardDetail {
  const card = fixtureResponse().cards[0];
  return {
    card,
    events: [
      { at: '2026-10-19T08:00:00Z', endAt: '2026-10-19T08:10:00Z', kind: 'coding', label: 'core-agent-run', attempt: 1,
        model: 'GPT-5.6 Sol', level: 'high', cli: 'codex', host: 'local', input: 1000, output: 100, cacheRead: 500,
        cacheWrite: 0, reasoning: 40, total: 1600, costUsd: 0.5, priced: true, durationMs: 600_000, result: 'passed',
        calls: 3, source: 'ledger' },
      { at: '2026-10-19T08:12:00Z', endAt: null, kind: 'gate', label: 'post-build-test-gate', attempt: 1,
        model: 'unknown', level: 'unknown', cli: 'unknown', host: 'local', input: 0, output: 0, cacheRead: 0,
        cacheWrite: 0, reasoning: null, total: 0, costUsd: 0, priced: true, durationMs: 60_000, result: 'fail',
        calls: 1, source: 'pipeline' },
    ],
    freshness: { status: 'complete' },
    disclaimer: 'Theoretical cost at list prices.',
  };
}
