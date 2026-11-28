import { describe, expect, it } from 'vitest';

import type { UsageCli, UsageCockpitResponse, UsageRun } from './models/usage-cockpit.model';
import {
  buildUsageDetailView,
  cliAccountHref,
  describeLedgerScope,
  detailSectionId,
  formatCountdown,
  formatRemainingPct,
  groupRunsByModel,
  instantView,
  ledgerHref,
  ledgerScopeFromHash,
  roundingRemainder,
} from './usage-detail.util';
import { formatUsedPct } from './usage-chip.util';
import { usageTriggerKey } from './components/usage-detail-surface/usage-detail-surface';

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
      local: 'Tue 29 Sept, 09:00', utc: 'Tue 29 Sept, 07:00 UTC',
      text: 'Tue 29 Sept, 09:00 Europe/Berlin (Tue 29 Sept, 07:00 UTC)', iso: '2026-09-29T07:00:00.000Z',
    });
    // AGT-3794-F14: a UTC alias reads as UTC, once.
    expect(instantView('2026-09-29T07:00:00Z', 'Etc/UTC')?.text).toBe('Tue 29 Sept, 07:00 UTC');
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

  it('lands on the workspace ledger page; CLI links open the CLI account page without a ledger scope', () => {
    // AGT-3794-F14: the ledger is workspace-wide, so the settings rail must highlight the workspace page.
    expect(ledgerHref(scope)).toMatch(/^#\/workspace\/settings\/tokens&/);
    expect(cliAccountHref('Codex')).toBe('#/workspace/settings/tokens/codex');
    expect(cliAccountHref('claude')).toBe('#/workspace/settings/tokens/claude');
    expect(cliAccountHref('gemini')).toBeNull();
    expect(ledgerScopeFromHash(cliAccountHref('claude')!)).toBeNull();
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

  it('uses admission-week spend for workspace and project budget remaining', () => {
    const value = snapshot();
    value.cost.weeklyBudgetUsd = 100;
    value.cost.budgetWeekSpentUsd = 25;
    value.cost.projects[1].weeklyBudgetUsd = 50;
    value.cost.projects[1].budgetWeekSpentUsd = 10;
    const view = buildUsageDetailView(value, NOW).cost;
    expect(view.budgetWeeklyRemaining).toBe('$75.00 remaining');
    expect(view.budgetWeeklySpent).toBe('$25.00');
    expect(view.projects.find(p => p.key === 'p1')?.budgetRemaining).toBe('$40.00 remaining');
  });

  it('lists unattributed last and marks unknown rows as incomplete', () => {
    const view = buildUsageDetailView(snapshot(), NOW).cost;
    expect(view.projects.map(p => p.name)).toEqual(['Agent Studio', 'Docs', 'Unattributed one-shot jobs']);
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
    expect(codex.windows.map(w => [w.label, w.value, w.remaining, w.reset?.utc ?? null])).toEqual([
      ['Weekly', '15% used', '85% left', 'Tue 29 Sept, 07:00 UTC'],
      ['5-hour', 'N/A', 'N/A', null],
    ]);
  });

  it('states what is left next to what is used, adding up to 100', () => {
    expect([91, 91.04, 91.06, 99.96, 100, 104.2].map(used => [formatUsedPct(used), formatRemainingPct(used)])).toEqual([
      ['91%', '9% left'],
      ['91%', '9% left'],
      ['91.1%', '8.9% left'],
      ['100%', 'None left'],
      ['100%', 'None left'],
      ['104.2%', 'None left'],
    ]);
    expect(formatRemainingPct(null)).toBeNull();
  });

  it('uses structured source reasons without parsing chip display text', () => {
    const codex = { ...cli('codex', true), availability: { ...OK, status: 'unavailable', reason: 'Provider: delayed' } };
    const base = snapshot();
    const cost = { ...base.cost, coverage: { ...OK, status: 'partial', reason: 'Pricing: incomplete' } };
    const view = buildUsageDetailView({ ...base, clis: [codex], cost }, NOW);
    expect(view.clis[0].stateReason).toBe('Provider: delayed');
    expect(view.cost.coverageReason).toBe('Pricing: incomplete');
  });

  it('does not present ledger read time as a receipt when no receipt exists', () => {
    const base = snapshot();
    const cost = { ...base.cost, latestReceiptAt: null };
    expect(buildUsageDetailView({ ...base, cost }, NOW).cost.latestReceipt).toBeNull();
  });
});


describe('subscription detail identity', () => {
  it('keeps same-provider subscription labels distinct and lists unallocated provider runs once', () => {
    const accounts = [
      { ...cli('claude'), subscriptionKey: 'work' },
      { ...cli('claude', true), subscriptionKey: 'personal' },
      cli('claude'),
    ];
    const value = snapshot({ clis: accounts, runs: [run('shared', { effectiveCli: 'claude' })] });
    const view = buildUsageDetailView(value, NOW);
    expect(view.clis.map(account => account.name)).toEqual(['Claude · subscription personal', 'Claude · subscription work', 'Claude · local']);
    expect(view.clis.flatMap(account => account.modelGroups.flatMap(group => group.runs.map(item => item.id))))
      .toEqual(['shared']);
    expect(value.clis).toEqual(accounts);
    expect(buildUsageDetailView({ ...value, clis: [...accounts].reverse() }, NOW).clis.map(account => account.name).sort())
      .toEqual(view.clis.map(account => account.name).sort());
  });

  it('uses distinct compact labels for real subscription digests with a shared prefix', () => {
    const prefix = 'a'.repeat(8);
    const keys = [prefix + '1' + '0'.repeat(55), prefix + '2' + '0'.repeat(55)];
    const value = snapshot({ clis: keys.map(subscriptionKey => ({ ...cli('claude'), subscriptionKey })) });
    const view = buildUsageDetailView(value, NOW);
    expect(view.clis.map(account => account.name)).toEqual([
      `Claude · subscription ${prefix}1…`, `Claude · subscription ${prefix}2…`,
    ]);
    expect(view.clis.map(account => account.subscriptionKey)).toEqual(keys);
    expect(new Set(view.clis.map(account => account.key)).size).toBe(2);
  });

  it('keeps subscription case and punctuation in section and trigger identities', () => {
    const focuses = [null, 'local', 'work/a', 'work:a', 'Work/a', 'work%2Fa'].map(subscriptionKey => ({
      kind: 'cli' as const, cliId: 'Claude', subscriptionKey,
    }));
    expect(new Set(focuses.map(focus => detailSectionId('detail', focus))).size).toBe(focuses.length);
    expect(new Set(focuses.map(usageTriggerKey)).size).toBe(focuses.length);
    expect(detailSectionId('detail', { kind: 'cli', cliId: 'claude' })).toBe('detail-cli-claude');
    expect(usageTriggerKey({ kind: 'cli', cliId: 'claude' })).toBe('cli:claude');
  });
});
