import { describe, expect, it } from 'vitest';

import type { UsageCli, UsageCostProjection, UsageSlotPool } from './models/usage-cockpit.model';
import {
  buildCliChipView,
  buildCostChipView,
  buildSlotChipView,
  formatLocalWithUtc,
  formatUsdCompact,
  formatUsdExact,
  formatUsedPct,
} from './usage-chip.util';

const NOW = Date.parse('2026-09-25T14:58:00Z');
const ZONE = 'Europe/Berlin';

function cli(overrides: Partial<UsageCli> = {}): UsageCli {
  return {
    cliId: 'codex', primary: true, plan: 'Pro', source: '/status',
    windows: [
      { id: 'codex/weekly', label: 'Weekly', usedPct: 15, resetAtUtc: '2026-09-29T07:00:00Z', resetLabel: null, suspiciousReason: null },
      { id: 'codex/5-hour', label: '5-hour', usedPct: 32, resetAtUtc: '2026-09-25T16:30:00Z', resetLabel: null, suspiciousReason: null },
    ],
    fetchedAt: '2026-09-25T14:55:00Z', ttlSeconds: 600,
    suspicious: false, suspiciousReason: null, probeFailedAt: null,
    limited: false, limitedReason: null,
    availability: { status: 'complete', observedAt: '2026-09-25T14:55:00Z', ttlSeconds: 600 },
    ...overrides,
  };
}

function cost(overrides: Partial<UsageCostProjection> = {}): UsageCostProjection {
  return {
    currency: 'USD',
    calendar: {
      timeZone: ZONE, weekStart: 1,
      dayStartUtc: '2026-09-24T22:00:00Z', dayEndUtc: '2026-09-25T22:00:00Z',
      weekStartUtc: '2026-09-20T22:00:00Z', weekEndUtc: '2026-09-27T22:00:00Z',
    },
    todayUsd: 12.48, weekUsd: 68.2, projects: [],
    coverage: { status: 'complete', observedAt: '2026-09-25T14:57:00Z', ttlSeconds: 60 },
    pricingVersion: 'TokenEconomy/1', normalizationVersion: 'v1',
    ledgerEndpointTemplate: '/api/projects/{project}/token-usage/summary',
    ...overrides,
  };
}

describe('usage chip number formatting', () => {
  it('keeps at most one decimal and never clamps values above 100', () => {
    expect(formatUsedPct(15)).toBe('15%');
    expect(formatUsedPct(32.46)).toBe('32.5%');
    expect(formatUsedPct(0.04)).toBe('0%');
    expect(formatUsedPct(142.3)).toBe('142.3%');
    expect(formatUsedPct(1234.5)).toBe('1,234.5%');
    expect(formatUsedPct(null)).toBeNull();
  });

  it('shows exact USD below $10,000 and a complete compact amount above', () => {
    expect(formatUsdCompact(12.48)).toBe('$12.48');
    expect(formatUsdCompact(9999.99)).toBe('$9,999.99');
    expect(formatUsdCompact(12480.37)).toBe('$12.5K');
    expect(formatUsdCompact(1_234_567.89)).toBe('$1.2M');
    expect(formatUsdExact(12480.37)).toBe('$12,480.37');
    expect(formatUsdExact(0)).toBe('$0.00');
    expect(formatUsdCompact(null)).toBeNull();
  });

  it('renders workspace-local time with its UTC equivalent', () => {
    expect(formatLocalWithUtc('2026-09-29T07:00:00Z', ZONE))
      .toBe('Tue 29 Sept, 09:00 Europe/Berlin (Tue 29 Sept, 07:00 UTC)');
    expect(formatLocalWithUtc('2026-09-29T07:00:00Z', 'Not/AZone')).toBe('Tue 29 Sept, 07:00 UTC');
  });
});

describe('CLI chip view', () => {
  it('names both windows with expanded wording and ends with the action', () => {
    const view = buildCliChipView('codex', cli(), ZONE, NOW);
    expect(view.state).toBe('normal');
    expect([view.weekly.tag, view.weekly.value, view.session.tag, view.session.value]).toEqual(['WK', '15%', '5H', '32%']);
    expect(view.ariaLabel).toBe('Codex, weekly 15 percent used, current five-hour window 32 percent used. Open usage.');
    expect(view.detail).toContain('Weekly: 15% used, resets Tue 29 Sept, 09:00 Europe/Berlin (Tue 29 Sept, 07:00 UTC)');
  });

  it('never borrows the weekly value for an unreported session window', () => {
    const view = buildCliChipView('codex', cli({ windows: [cli().windows[0]] }), ZONE, NOW);
    expect(view.session).toMatchObject({ tag: 'Session', value: 'N/A', reported: false });
    expect(view.ariaLabel).toContain('current session not reported');
    expect(view.ariaLabel).not.toMatch(/session[^.]*0 percent/);
  });

  it('uses 5H only when the provider reports a five-hour window', () => {
    const windows = [cli().windows[0], { ...cli().windows[1], label: 'Current session' }];
    expect(buildCliChipView('codex', cli({ windows }), ZONE, NOW).session.tag).toBe('Session');
  });

  it('finds the session window with the same five-hour wording that picks the 5H tag', () => {
    for (const label of ['5h', '5-hour', 'Five-hour window', 'Current session']) {
      const windows = [cli().windows[0], { ...cli().windows[1], label }];
      const view = buildCliChipView('codex', cli({ windows }), ZONE, NOW);
      expect(view.session, label).toMatchObject({ value: '32%', reported: true });
      expect(view.session.tag, label).toBe(label === 'Current session' ? 'Session' : '5H');
    }
  });

  it('keeps values above 100 and fractional percentages exact', () => {
    const windows = [{ ...cli().windows[0], usedPct: 104.25 }, { ...cli().windows[1], usedPct: 7.5 }];
    const view = buildCliChipView('codex', cli({ windows }), ZONE, NOW);
    expect([view.weekly.value, view.session.value]).toEqual(['104.3%', '7.5%']);
    expect(view.ariaLabel).toContain('weekly 104.3 percent used');
  });

  it('prefers the all-models weekly window and ignores Spark buckets', () => {
    const windows = [
      { id: 'codex/spark-weekly', label: 'Spark weekly', usedPct: 90, resetAtUtc: null, resetLabel: null, suspiciousReason: null },
      { id: 'claude/weekly-sonnet', label: 'Weekly (Sonnet)', usedPct: 60, resetAtUtc: null, resetLabel: null, suspiciousReason: null },
      { id: 'claude/weekly-all', label: 'Weekly (all models)', usedPct: 7, resetAtUtc: null, resetLabel: null, suspiciousReason: null },
    ];
    expect(buildCliChipView('claude', cli({ cliId: 'claude', windows }), ZONE, NOW).weekly.value).toBe('7%');
  });

  it('reports loading without any numeric placeholder', () => {
    const view = buildCliChipView('claude', null, ZONE, NOW);
    expect(view.state).toBe('loading');
    expect(view.weekly.value).toBe('');
    expect(view.ariaLabel).toBe('Claude, usage loading. Open usage.');
  });

  it('marks an unavailable CLI as unknown with its reason', () => {
    const view = buildCliChipView('gemini', cli({
      cliId: 'gemini', windows: [], fetchedAt: null,
      availability: { status: 'unavailable', observedAt: null, ttlSeconds: 600, reason: 'CLI not installed' },
    }), ZONE, NOW);
    expect(view.state).toBe('unknown');
    expect(view.ariaLabel).toBe('Gemini, usage unavailable. Unavailable: CLI not installed. Open usage.');
  });

  it('marks stale on TTL expiry or a failed probe, with the last update time', () => {
    const old = buildCliChipView('codex', cli({ fetchedAt: '2026-09-25T14:00:00Z' }), ZONE, NOW);
    expect(old.state).toBe('stale');
    expect(old.ariaLabel).toContain('Stale: The snapshot is older than its refresh interval.');
    expect(old.ariaLabel).toContain('Last updated Fri 25 Sept, 16:00 Europe/Berlin');
    expect(buildCliChipView('codex', cli({ probeFailedAt: '2026-09-25T14:57:00Z' }), ZONE, NOW).state).toBe('stale');
  });

  it('lets suspicious outrank stale', () => {
    const view = buildCliChipView('codex', cli({
      suspicious: true, suspiciousReason: 'Implausible drop awaiting confirmation', probeFailedAt: '2026-09-25T14:57:00Z',
    }), ZONE, NOW);
    expect(view.state).toBe('suspicious');
    expect(view.ariaLabel).toContain('Unverified: Implausible drop awaiting confirmation.');
  });

  it('keeps a long provider name whole', () => {
    const view = buildCliChipView('enterprise-gateway-provider', cli({ cliId: 'enterprise-gateway-provider' }), ZONE, NOW);
    expect(view.name).toBe('enterprise-gateway-provider');
  });
});

describe('cost chip view', () => {
  it('shows the ledger estimate and says so in the accessible name', () => {
    const view = buildCostChipView(cost(), NOW);
    expect(view).toMatchObject({ state: 'normal', value: '$12.48', exact: '$12.48' });
    expect(view.ariaLabel).toBe("Today's cost, $12.48 USD ledger estimate. Open cost detail.");
    expect(view.detail).toContain('Day starts Fri 25 Sept, 00:00 Europe/Berlin (Thu 24 Sept, 22:00 UTC)');
  });

  it('keeps the exact amount for large totals', () => {
    const view = buildCostChipView(cost({ todayUsd: 12480.37 }), NOW);
    expect(view.value).toBe('$12.5K');
    expect(view.ariaLabel).toContain('$12,480.37 USD');
  });

  it('never shows an unknown amount as zero', () => {
    const view = buildCostChipView(cost({
      todayUsd: null, coverage: { status: 'unavailable', observedAt: null, ttlSeconds: 60, reason: 'The ledger could not be read.' },
    }), NOW);
    expect(view).toMatchObject({ state: 'unknown', value: 'N/A', exact: null });
    expect(view.ariaLabel).toBe("Today's cost, unavailable. Unavailable: The ledger could not be read. Open cost detail.");
  });

  it('keeps a real complete zero distinct from unknown', () => {
    expect(buildCostChipView(cost({ todayUsd: 0 }), NOW)).toMatchObject({ state: 'normal', value: '$0.00' });
  });

  it('labels partial coverage and stale ledger snapshots', () => {
    expect(buildCostChipView(cost({ coverage: { status: 'partial', observedAt: '2026-09-25T14:57:00Z', ttlSeconds: 60 } }), NOW).state).toBe('partial');
    expect(buildCostChipView(cost({ coverage: { status: 'complete', observedAt: '2026-09-25T14:00:00Z', ttlSeconds: 60 } }), NOW).state).toBe('stale');
  });

  it('exposes only the fields the cost chip template renders', () => {
    expect(Object.keys(buildCostChipView(cost(), NOW)).sort()).toEqual(['alarm', 'ariaLabel', 'detail', 'exact', 'state', 'value']);
    expect(Object.keys(buildCostChipView(null, NOW)).sort()).toEqual(['alarm', 'ariaLabel', 'detail', 'exact', 'state', 'value']);
  });

  it('reports loading without $0.00', () => {
    expect(buildCostChipView(null, NOW)).toMatchObject({ state: 'loading', value: '', exact: null });
  });
});

describe('slot chip view', () => {
  const pool = (name: string, occupied: number | null, capacity: number | null, status = 'complete'): UsageSlotPool =>
    ({ name, occupied, capacity, availability: { status, observedAt: null, ttlSeconds: null } });

  it('keeps each pool separate and never sums them', () => {
    const view = buildSlotChipView([pool('remote', 2, 3), pool('review', 1, 3), pool('auto', 10, 17)]);
    expect(view.pools.map(p => `${p.name} ${p.value}`)).toEqual(['remote 2/3', 'review 1/3', 'auto 10/17']);
    expect(view.ariaLabel).toBe('Slots, remote 2 of 3 in use, review 1 of 3 in use, auto 10 of 17 in use. Show slot pools.');
  });

  it('reads an unavailable pool as N/A, not 0', () => {
    const view = buildSlotChipView([pool('remote', null, null, 'unavailable'), pool('review', 0, 3)]);
    expect(view.state).toBe('partial');
    expect(view.pools.map(p => p.value)).toEqual(['N/A', '0/3']);
  });
});
