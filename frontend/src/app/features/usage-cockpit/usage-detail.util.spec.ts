import { describe, expect, it } from 'vitest';

import type { UsageCli, UsageCockpitResponse, UsageRun } from './models/usage-cockpit.model';
import {
  buildUsageDetailView,
  describeLedgerScope,
  formatCountdown,
  groupRunsByModel,
  instantView,
  ledgerHref,
  ledgerScopeFromHash,
  roundingRemainder,
} from './usage-detail.util';

const NOW = Date.parse('2026-09-25T14:58:00Z');
const ZONE = 'Europe/Berlin';
const CALENDAR = {
  timeZone: ZONE, weekStart: 1,
  dayStartUtc: '2026-09-24T22:00:00Z', dayEndUtc: '2026-09-25T22:00:00Z',
  weekStartUtc: '2026-09-20T22:00:00Z', weekEndUtc: '2026-09-27T22:00:00Z',
};
const OK = { status: 'complete', observedAt: '2026-09-25T14:57:00Z', ttlSeconds: 60 };

function cli(cliId: string, primary = false): UsageCli {
  return {
    cliId, primary, plan: 'Pro', source: '/status',
    windows: [
      { id: `${cliId}/weekly`, label: 'Weekly', usedPct: 15, resetAtUtc: '2026-09-29T07:00:00Z', resetLabel: null, suspiciousReason: null },
      { id: `${cliId}/5-hour`, label: '5-hour', usedPct: null, resetAtUtc: null, resetLabel: null, suspiciousReason: null },
    ],
    fetchedAt: '2026-09-25T14:55:00Z', ttlSeconds: 600,
    suspicious: false, suspiciousReason: null, probeFailedAt: null,
    limited: false, limitedReason: null, availability: OK,
  };
}

function run(id: string, overrides: Partial<UsageRun> = {}): UsageRun {
  return {
    id, projectId: 'p1', taskId: id, taskKey: id.toUpperCase(), startedAt: '2026-09-25T14:00:00Z',
    provisionalCostUsd: 1, includedInTotals: true,
    configuredCli: 'codex', configuredModel: 'gpt-5.5', configuredReasoning: 'high',
    effectiveCli: 'codex', effectiveModel: 'gpt-5.5', effectiveReasoning: 'high',
    fallbackReason: null, ...overrides,
  };
}

function snapshot(overrides: Partial<UsageCockpitResponse> = {}): UsageCockpitResponse {
  return {
    snapshotVersion: 1, workspaceId: 'ws 1', timeZone: ZONE, weekStart: 1,
    generatedAt: '2026-09-25T14:57:00Z', calendar: CALENDAR,
    clis: [cli('claude'), cli('codex', true)],
    cost: {
      currency: 'USD', calendar: CALENDAR, todayUsd: 12.48, weekUsd: 68.2,
      projects: [
        { projectId: null, name: 'x', todayUsd: 1.234, weekUsd: 6.534, coverage: OK },
        { projectId: 'p1', name: 'Agent Studio', todayUsd: 8.123, weekUsd: 41.333, coverage: OK },
        { projectId: 'p2', name: 'Docs', todayUsd: 3.123, weekUsd: 20.333, coverage: OK },
      ],
      coverage: OK, pricingVersion: 'v', normalizationVersion: 'n',
      ledgerEndpointTemplate: '/api/projects/{project}/token-usage/summary',
    },
    runs: [], slots: [], sources: {}, ...overrides,
  };
}

describe('instants and countdowns', () => {
  it('shows workspace-local and UTC separately', () => {
    expect(instantView('2026-09-29T07:00:00Z', ZONE)).toEqual({
      local: 'Tue 29 Sept, 09:00', utc: 'Tue 29 Sept, 07:00 UTC', iso: '2026-09-29T07:00:00.000Z',
    });
    expect(instantView(null, ZONE)).toBeNull();
    expect(instantView('nonsense', ZONE)).toBeNull();
  });

  it('counts down to the reset and never goes negative', () => {
    expect(formatCountdown('2026-09-25T16:30:00Z', NOW)).toBe('in 1 h 32 min');
    expect(formatCountdown('2026-09-29T07:00:00Z', NOW)).toBe('in 3 d 16 h');
    expect(formatCountdown('2026-09-25T14:00:00Z', NOW)).toBe('due');
  });
});

describe('ledger links', () => {
  const scope = { workspaceId: 'ws 1', range: 'today' as const, fromUtc: CALENDAR.dayStartUtc, toUtc: CALENDAR.dayEndUtc, timeZone: ZONE };

  it('round-trips workspace, range, zone and project through the hash', () => {
    const href = ledgerHref({ ...scope, projectId: 'p1' });
    expect(href.startsWith('#/workspace/settings/tokens&ledger-workspace=ws%201&ledger-range=today')).toBe(true);
    expect(ledgerScopeFromHash(href)).toEqual({ ...scope, projectId: 'p1' });
  });

  it('opens an existing CLI page and falls back to the workspace page', () => {
    expect(ledgerHref(scope, 'Codex')).toMatch(/^#\/workspace\/settings\/tokens\/codex&/);
    expect(ledgerHref(scope, 'gemini')).toMatch(/^#\/workspace\/settings\/tokens&/);
  });

  it('rejects incomplete or malformed scopes', () => {
    expect(ledgerScopeFromHash('#/workspace/settings/tokens')).toBeNull();
    expect(ledgerScopeFromHash('#/workspace/settings/tokens&ledger-workspace=a&ledger-range=month&ledger-from=x&ledger-to=y')).toBeNull();
  });

  it('describes the scope in local time with UTC', () => {
    expect(describeLedgerScope({ ...scope, projectId: 'unattributed' })).toBe(
      'Today, workspace ws 1, unattributed spend: Fri 25 Sept, 00:00 to Sat 26 Sept, 00:00 Europe/Berlin '
      + '(Thu 24 Sept, 22:00 UTC to Fri 25 Sept, 22:00 UTC)');
  });
});

describe('reconciliation', () => {
  it('adds an explicit rounding remainder only when cents disagree', () => {
    expect(roundingRemainder(12.48, [8.123, 3.123, 1.234])).toBe(0.01);
    expect(roundingRemainder(10, [4, 6])).toBeNull();
    expect(roundingRemainder(10, [4, null])).toBeNull();
    expect(roundingRemainder(null, [4])).toBeNull();
  });

  it('lists unattributed last and marks unknown rows as incomplete', () => {
    const view = buildUsageDetailView(snapshot(), NOW).cost;
    expect(view.projects.map(p => p.name)).toEqual(['Agent Studio', 'Docs', 'Unattributed']);
    expect(view.reconcile).toEqual({ todayRemainder: '$0.01', weekRemainder: '$0.01', complete: true });

    const partial = snapshot();
    partial.cost.projects[0].todayUsd = null;
    const pv = buildUsageDetailView(partial, NOW).cost;
    expect(pv.reconcile.complete).toBe(false);
    expect(pv.projects.find(p => p.unattributed)!.today).toBe('N/A');
  });
});

describe('live runs and models', () => {
  it('labels inclusion and shows Pending instead of zero', () => {
    const view = buildUsageDetailView(snapshot({
      runs: [run('a'), run('b', { provisionalCostUsd: null, includedInTotals: false })],
    }), NOW).cost;
    expect(view.liveRuns.map(r => [r.spend, r.inclusion])).toEqual([
      ['$1.00', "Included in today's total"],
      ['Pending', 'Not yet in totals'],
    ]);
  });

  it('groups by effective model and reasoning; unknown is never inherited', () => {
    const groups = groupRunsByModel([
      run('a'),
      run('b'),
      run('c', { effectiveReasoning: '' }),
      run('d', { effectiveModel: 'gpt-5.4-mini', effectiveReasoning: 'medium' }),
      run('e', { effectiveModel: 'unknown', effectiveReasoning: 'high' }),
    ], new Map());
    expect(groups.map(g => [g.model, g.reasoning, g.runs.length])).toEqual([
      ['gpt-5.4-mini', 'medium', 1],
      ['gpt-5.5', 'high', 2],
      ['gpt-5.5', 'Unknown', 1],
      ['Unknown', 'high', 1],
    ]);
    expect(groups[0].runs[0].configuredDifference).toBe('Configured codex / gpt-5.5 / high');
    expect(groups[1].runs[0].configuredDifference).toBeNull();
    expect(groups[2].runs[0].configuredDifference).toBe('Configured codex / gpt-5.5 / high');
  });

  it('keeps runs inside their own CLI and surfaces orphans', () => {
    const view = buildUsageDetailView(snapshot({
      runs: [run('a'), run('b', { effectiveCli: 'claude', effectiveModel: 'opus' }), run('c', { effectiveCli: '' })],
    }), NOW);
    expect(view.clis.map(c => c.cliId)).toEqual(['codex', 'claude']);
    expect(view.clis[0].modelGroups.flatMap(g => g.runs.map(r => r.id))).toEqual(['a']);
    expect(view.clis[1].modelGroups.flatMap(g => g.runs.map(r => r.id))).toEqual(['b']);
    expect(view.unknownCliGroups.flatMap(g => g.runs.map(r => r.id))).toEqual(['c']);
  });

  it('lists every window and keeps unreported ones explicit', () => {
    const codex = buildUsageDetailView(snapshot(), NOW).clis[0];
    expect(codex.windows.map(w => [w.label, w.value, w.reset?.utc ?? null])).toEqual([
      ['Weekly', '15% used', 'Tue 29 Sept, 07:00 UTC'],
      ['5-hour', 'Not reported', null],
    ]);
  });

  it('uses structured source reasons without parsing chip display text', () => {
    const codex = { ...cli('codex', true), availability: { ...OK, status: 'unavailable', reason: 'Provider: delayed' } };
    const base = snapshot();
    const cost = { ...base.cost, coverage: { ...OK, status: 'partial', reason: 'Pricing: incomplete' } };
    const view = buildUsageDetailView({ ...base, clis: [codex], cost }, NOW);
    expect(view.clis[0].stateReason).toBe('Provider: delayed');
    expect(view.cost.coverageReason).toBe('Pricing: incomplete');
  });
});
