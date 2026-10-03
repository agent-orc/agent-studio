import type {
  UsageCli,
  UsageCliWindow,
  UsageCostProjection,
  UsageSlotPool,
} from '../../../app/features/usage-cockpit';

/**
 * Synthetic HUC-S2 fixtures pinned to the Dossier reference time,
 * 25 September 2026 16:58 Europe/Berlin. Not live telemetry.
 */
export const FIXTURE_NOW = Date.parse('2026-09-25T14:58:00Z');
export const FIXTURE_ZONE = 'Europe/Berlin';

function win(id: string, label: string, usedPct: number | null, resetAtUtc: string | null = null): UsageCliWindow {
  return { id, label, usedPct, resetAtUtc, resetLabel: null, suspiciousReason: null };
}

function cli(cliId: string, windows: UsageCliWindow[], overrides: Partial<UsageCli> = {}): UsageCli {
  return {
    cliId, primary: cliId === 'codex', plan: 'Pro', source: '/status', windows,
    fetchedAt: '2026-09-25T14:55:00Z', ttlSeconds: 600,
    suspicious: false, suspiciousReason: null, probeFailedAt: null,
    limited: false, limitedReason: null,
    availability: { status: 'complete', observedAt: '2026-09-25T14:55:00Z', ttlSeconds: 600 },
    ...overrides,
  };
}

const codexWindows = [
  win('codex/weekly', 'Weekly', 15, '2026-09-29T07:00:00Z'),
  win('codex/5-hour', '5-hour', 32, '2026-09-25T16:30:00Z'),
];
const claudeWindows = [
  win('claude/weekly-(all-models)', 'Weekly (all models)', 7, '2026-09-30T08:00:00Z'),
  win('claude/current-session-(5h)', 'Current session (5h)', 19, '2026-09-25T18:00:00Z'),
];

export const CODEX = cli('codex', codexWindows);
export const CLAUDE = cli('claude', claudeWindows);

export const CODEX_STALE = cli('codex', codexWindows, {
  fetchedAt: '2026-09-25T13:40:00Z',
  probeFailedAt: '2026-09-25T14:50:00Z',
  availability: { status: 'stale', observedAt: '2026-09-25T13:40:00Z', ttlSeconds: 600 },
});
export const CLAUDE_SUSPICIOUS = cli('claude', claudeWindows, {
  suspicious: true,
  suspiciousReason: 'Implausible downward jump awaiting a confirmation probe',
  availability: { status: 'suspicious', observedAt: '2026-09-25T14:55:00Z', ttlSeconds: 600 },
});
export const GEMINI_UNAVAILABLE = cli('gemini', [], {
  fetchedAt: null, plan: null,
  availability: { status: 'unavailable', observedAt: null, ttlSeconds: 600, reason: 'No quota snapshot has been recorded' },
});
/** Weekly reported, current session not supported by the provider. */
export const CODEX_NO_SESSION = cli('codex', [codexWindows[0]]);

export const CODEX_OVER = cli('codex', [
  win('codex/weekly', 'Weekly', 104.25, '2026-09-29T07:00:00Z'),
  win('codex/5-hour', '5-hour', 137, '2026-09-25T16:30:00Z'),
]);
export const CLAUDE_FRACTIONAL = cli('claude', [
  win('claude/weekly-(all-models)', 'Weekly (all models)', 7.46, '2026-09-30T08:00:00Z'),
  win('claude/current-session', 'Current session', 0.4, '2026-09-25T18:00:00Z'),
]);
export const LONG_PROVIDER = cli('enterprise-gateway-provider', [
  win('enterprise-gateway-provider/weekly', 'Weekly', 64.5),
  win('enterprise-gateway-provider/session', 'Current session (5h)', 12),
]);

function cost(todayUsd: number | null, overrides: Partial<UsageCostProjection> = {}): UsageCostProjection {
  return {
    currency: 'USD',
    calendar: {
      timeZone: FIXTURE_ZONE, weekStart: 1,
      dayStartUtc: '2026-09-24T22:00:00Z', dayEndUtc: '2026-09-25T22:00:00Z',
      weekStartUtc: '2026-09-20T22:00:00Z', weekEndUtc: '2026-09-27T22:00:00Z',
    },
    todayUsd, weekUsd: todayUsd == null ? null : todayUsd * 5.5, projects: [],
    coverage: { status: 'complete', observedAt: '2026-09-25T14:57:00Z', ttlSeconds: 60 },
    pricingVersion: 'TokenEconomy/fixture', normalizationVersion: 'openai-input-includes-cached/v1',
    ledgerEndpointTemplate: '/api/projects/{project}/token-usage/summary',
    ...overrides,
  };
}

export const COST = cost(12.48);
export const COST_ZERO = cost(0);
export const COST_LARGE = cost(12480.37);
export const COST_HUGE = cost(1234567.89);
export const COST_UNKNOWN = cost(null, {
  coverage: { status: 'unavailable', observedAt: null, ttlSeconds: 60, reason: 'The token ledger could not be read' },
});
export const COST_STALE = cost(12.48, {
  coverage: { status: 'complete', observedAt: '2026-09-25T14:20:00Z', ttlSeconds: 60 },
});
export const COST_PARTIAL = cost(9.12, {
  coverage: { status: 'partial', observedAt: '2026-09-25T14:57:00Z', ttlSeconds: 60, reason: 'Some models have no historical USD price' },
});

const pool = (name: string, occupied: number | null, capacity: number | null, status = 'complete'): UsageSlotPool =>
  ({ name, occupied, capacity, availability: { status, observedAt: '2026-09-25T14:57:00Z', ttlSeconds: 120 } });

export const SLOTS = [pool('remote', 2, 3), pool('review', 1, 3), pool('auto', 10, 17)];
export const SLOTS_PARTIAL = [pool('remote', null, null, 'unavailable'), pool('review', 1, 3), pool('auto', 10, 17)];
