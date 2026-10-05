import { describe, expect, it } from 'vitest';

import type { UsageCli, UsageCockpitResponse, UsageCostProjection } from './models/usage-cockpit.model';
import {
  EMPTY_USAGE_ALARM_STATE,
  hiddenProviderAlarms,
  observeCli,
  observeCost,
  observeSnapshot,
  reduceUsageAlarms,
  type UsageAlarmState,
} from './usage-alarm.policy';
import { buildCliChipView, buildCostChipView } from './usage-chip.util';

const NOW = Date.parse('2026-09-25T14:58:00Z');
const ZONE = 'Europe/Berlin';
const WEEKLY_RESET = '2026-09-29T07:00:00Z';
const SESSION_RESET = '2026-09-25T16:30:00Z';

function cli(weekly: number | null, session: number | null = 32, overrides: Partial<UsageCli> = {}): UsageCli {
  return {
    cliId: 'codex', primary: true, plan: 'Pro', source: '/status',
    windows: [
      { id: 'codex/weekly', label: 'Weekly', usedPct: weekly, resetAtUtc: WEEKLY_RESET, resetLabel: null, suspiciousReason: null },
      { id: 'codex/5-hour', label: '5-hour', usedPct: session, resetAtUtc: SESSION_RESET, resetLabel: null, suspiciousReason: null },
    ],
    fetchedAt: '2026-09-25T14:55:00Z', ttlSeconds: 600,
    suspicious: false, suspiciousReason: null, probeFailedAt: null,
    limited: false, limitedReason: null,
    availability: { status: 'complete', observedAt: '2026-09-25T14:55:00Z', ttlSeconds: 600 },
    ...overrides,
  };
}

const STALE: Partial<UsageCli> = {
  fetchedAt: '2026-09-25T13:40:00Z', probeFailedAt: '2026-09-25T14:50:00Z',
  availability: { status: 'stale', observedAt: '2026-09-25T13:40:00Z', ttlSeconds: 600 },
};
const SUSPICIOUS: Partial<UsageCli> = {
  suspicious: true, suspiciousReason: 'Implausible downward jump',
  availability: { status: 'suspicious', observedAt: '2026-09-25T14:55:00Z', ttlSeconds: 600 },
};

function cost(today: number | null, week: number | null, overrides: Partial<UsageCostProjection> = {}): UsageCostProjection {
  return {
    currency: 'USD',
    calendar: {
      timeZone: ZONE, weekStart: 1,
      dayStartUtc: '2026-09-24T22:00:00Z', dayEndUtc: '2026-09-25T22:00:00Z',
      weekStartUtc: '2026-09-20T22:00:00Z', weekEndUtc: '2026-09-27T22:00:00Z',
    },
    todayUsd: today, weekUsd: week, projects: [],
    coverage: { status: 'complete', observedAt: '2026-09-25T14:57:00Z', ttlSeconds: 60 },
    pricingVersion: 'TokenEconomy/1', normalizationVersion: 'v1',
    ledgerEndpointTemplate: '/api/projects/{project}/token-usage/summary',
    ...overrides,
  };
}

function snapshot(clis: UsageCli[], costProjection: UsageCostProjection | null = null, workspaceId = 'ws-1'): UsageCockpitResponse {
  return {
    snapshotVersion: 1, workspaceId, timeZone: ZONE, weekStart: 1, generatedAt: '2026-09-25T14:58:00Z',
    calendar: cost(0, 0).calendar, clis, cost: costProjection as UsageCostProjection, runs: [], slots: [], sources: {},
  };
}

/** Feed snapshots in order and collect every step's announcements. */
function run(steps: [UsageCockpitResponse, number][], start: UsageAlarmState = EMPTY_USAGE_ALARM_STATE) {
  let state = start;
  const spoken: string[][] = [];
  for (const [snap, at] of steps) {
    const step = reduceUsageAlarms(state, snap.workspaceId, observeSnapshot(snap, at), at);
    state = step.state;
    spoken.push(step.announcements);
  }
  return { state, spoken };
}

const kinds = (c: UsageCli) => observeCli(c, NOW).alarms.map(a => `${a.kind}:${a.window ?? '-'}`);

describe('quota warning threshold (strictly above 80 percent)', () => {
  it('treats 80 as headroom and 80.1 as a warning', () => {
    expect(kinds(cli(80, 80))).toEqual([]);
    expect(kinds(cli(80.1, 12))).toEqual(['quota:codex/weekly']);
    expect(kinds(cli(12, 80.1))).toEqual(['quota:codex/5-hour']);
  });

  it('warns for values above 100 without clamping them', () => {
    const [weekly] = observeCli(cli(137.5, 10), NOW).alarms;
    expect(weekly.label).toBe('Weekly 137.5% used');
    expect(weekly.sentence).toContain('137.5 percent used');
  });

  it('keys each window with its own reset cycle', () => {
    const alarms = observeCli(cli(91, 95), NOW).alarms;
    expect(alarms.map(a => a.cycleEndsAt)).toEqual([Date.parse(WEEKLY_RESET), Date.parse(SESSION_RESET)]);
  });

  it('does not confirm a warning from stale, suspicious or unavailable readings', () => {
    expect(kinds(cli(95, 95, STALE))).toEqual([]);
    expect(kinds(cli(95, 95, SUSPICIOUS))).toEqual([]);
    expect(kinds(cli(95, 95, { availability: { status: 'unavailable', observedAt: null, ttlSeconds: 600 } }))).toEqual([]);
    const oneSuspiciousWindow = cli(95, 20);
    oneSuspiciousWindow.windows[1].suspiciousReason = 'Reset without a new window';
    expect(kinds(oneSuspiciousWindow)).toEqual([]);
    // Untrusted samples cannot clear a quota alarm either.
    expect(observeCli(cli(10, 10, STALE), NOW).clears).not.toContain('quota');
  });

  it('treats a sample older than its TTL as untrusted even when marked complete', () => {
    const old = cli(95, 20, { fetchedAt: '2026-09-25T14:40:00Z' });
    expect(kinds(old)).toEqual([]);
    expect(observeCli(old, NOW).clears).toEqual([]);
  });
});

describe('provider limited', () => {
  it('is a critical alarm at low percentages and without any quota data', () => {
    expect(kinds(cli(3, 1, { limited: true, limitedReason: 'Rate limit reached' }))).toEqual(['limited:-']);
    const unknownQuota = cli(null, null, { limited: true, limitedReason: null, availability: { status: 'unavailable', observedAt: null, ttlSeconds: 600 } });
    const [limit] = observeCli(unknownQuota, NOW).alarms;
    expect(limit.severity).toBe('critical');
    expect(limit.sentence).toBe('Codex is limited by the provider.');
  });

  it('makes no claim when the limit source could not be read', () => {
    const observation = observeCli(cli(10, 10, { limited: null }), NOW);
    expect(observation.alarms).toEqual([]);
    expect(observation.clears).toEqual(['quota']);
    expect(observeCli(cli(10, 10), NOW).clears).toEqual(['limited', 'quota']);
  });

  it('outranks a simultaneous quota warning on the chip, keeping both causes', () => {
    const c = cli(92, 40, { limited: true, limitedReason: 'Admission paused' });
    const { state } = run([[snapshot([c]), NOW]]);
    const view = buildCliChipView('codex', c, ZONE, NOW, Object.values(state.active));
    expect(view.alarm).toBe('limited');
    expect(view.alarmLabel).toBe('Limited');
    expect(view.ariaLabel).toMatch(/Limited: Codex is limited by the provider: Admission paused\. Warning: Codex weekly quota 92 percent used/);
    expect(view.detail).toContain('Limited: Admission paused');
    expect(view.detail).toContain('Weekly 92% used');
  });
});

describe('budget crossed', () => {
  const budgets = { dailyBudgetUsd: 50, weeklyBudgetUsd: 200 };
  const budgetKinds = (c: UsageCostProjection, at = NOW) => observeCost(c, at).alarms.map(a => a.window);

  it('never alarms when no budget is configured', () => {
    expect(budgetKinds(cost(9_999, 99_999))).toEqual([]);
    expect(buildCostChipView(cost(9_999, 99_999), NOW).ariaLabel).not.toMatch(/budget/i);
  });

  it('treats exact equality as within budget', () => {
    expect(budgetKinds(cost(50, 200, budgets))).toEqual([]);
    expect(buildCostChipView(cost(50, 200, budgets), NOW).ariaLabel).toContain('Within budget.');
  });

  it('warns on a daily overrun and on a weekly overrun independently', () => {
    expect(budgetKinds(cost(50.01, 120, budgets))).toEqual(['daily']);
    expect(budgetKinds(cost(12, 200.5, budgets))).toEqual(['weekly']);
    expect(budgetKinds(cost(60, 250, budgets))).toEqual(['daily', 'weekly']);
    const [daily] = observeCost(cost(50.01, 120, budgets), NOW).alarms;
    expect(daily.sentence).toBe("Today's cost $50.01 USD exceeds the daily budget of $50.00 USD.");
    expect(daily.cycleEndsAt).toBe(Date.parse('2026-09-25T22:00:00Z'));
  });

  it('confirms an overrun from a partial total (a lower bound) but never clears on one', () => {
    const partial = cost(51, 120, { ...budgets, coverage: { status: 'partial', observedAt: '2026-09-25T14:57:00Z', ttlSeconds: 60, reason: 'Unpriced model' } });
    expect(budgetKinds(partial)).toEqual(['daily']);
    const under = { ...partial, todayUsd: 20 };
    expect(observeCost(under, NOW).clears).toEqual([]);
    const view = buildCostChipView(under, NOW);
    expect(view.alarm).toBeNull();
    expect(view.ariaLabel).toContain('an overrun may not be detected yet');
    expect(view.ariaLabel).not.toContain('Within budget');
  });

  it('never claims safety for unknown, stale or suspicious ledgers', () => {
    const unknown = cost(null, null, { ...budgets, coverage: { status: 'unavailable', observedAt: null, ttlSeconds: 60 } });
    const stale = cost(20, 80, { ...budgets, coverage: { status: 'complete', observedAt: '2026-09-25T14:20:00Z', ttlSeconds: 60 } });
    const suspicious = cost(80, 80, { ...budgets, coverage: { status: 'suspicious', observedAt: '2026-09-25T14:57:00Z', ttlSeconds: 60 } });
    for (const c of [unknown, stale, suspicious]) {
      expect(observeCost(c, NOW).clears).toEqual([]);
      expect(buildCostChipView(c, NOW).ariaLabel).toContain('Budget: not confirmed');
    }
    expect(budgetKinds(suspicious)).toEqual([]);
  });

  it('does not claim safety or clear a weekly alarm when the weekly total is missing', () => {
    const incomplete = cost(20, null, { ...budgets });
    expect(buildCostChipView(incomplete, NOW).ariaLabel).toContain('Budget: not confirmed');
    const { state } = run([[snapshot([], cost(20, 250, budgets)), NOW]]);
    const later = run([[snapshot([], incomplete), NOW + 1_000]], state);
    expect(Object.values(later.state.active).some(a => a.window === 'weekly')).toBe(true);
    expect(later.spoken).toEqual([[]]);
  });

  it('clears a daily alarm on a trusted daily total even when the weekly total is missing', () => {
    const { state } = run([[snapshot([], cost(60, 120, budgets)), NOW]]);
    expect(Object.values(state.active).map(a => a.window)).toEqual(['daily']);
    const recovered = run([[snapshot([], cost(20, null, budgets)), NOW]], state);
    expect(recovered.state.active).toEqual({});
    expect(recovered.spoken).toEqual([[]]);
    expect(observeCost(cost(20, null, budgets), NOW).clearKeys).toEqual(['cost/budget/daily']);
  });

  it('clears each budget period on its own trusted total and keeps the other latched', () => {
    const { state } = run([[snapshot([], cost(60, 250, budgets)), NOW]]);
    expect(Object.values(state.active).map(a => a.window).sort()).toEqual(['daily', 'weekly']);
    const dailyOnly = run([[snapshot([], cost(20, null, budgets)), NOW]], state);
    expect(Object.values(dailyOnly.state.active).map(a => a.window)).toEqual(['weekly']);
    const weeklyOnly = run([[snapshot([], cost(null, 150, budgets)), NOW]], state);
    expect(Object.values(weeklyOnly.state.active).map(a => a.window)).toEqual(['daily']);
  });

  it('does not clear a daily alarm from a partial or stale ledger even with a low daily total', () => {
    const { state } = run([[snapshot([], cost(60, 120, budgets)), NOW]]);
    const partial = cost(20, null, { ...budgets, coverage: { status: 'partial', observedAt: '2026-09-25T14:57:00Z', ttlSeconds: 60 } });
    const stale = cost(20, null, { ...budgets, coverage: { status: 'complete', observedAt: '2026-09-25T14:20:00Z', ttlSeconds: 60 } });
    for (const c of [partial, stale]) {
      expect(Object.values(run([[snapshot([], c), NOW + 1_000]], state).state.active).map(a => a.window)).toEqual(['daily']);
    }
  });

  it('ignores a total from a period that has already ended', () => {
    // At the local midnight the day has ended; the week (budget 200) has not been exceeded.
    expect(budgetKinds(cost(80, 80, budgets), Date.parse('2026-09-25T22:00:00Z'))).toEqual([]);
    expect(budgetKinds(cost(80, 280, budgets), Date.parse('2026-09-27T22:00:00Z'))).toEqual([]);
  });
});

describe('transitions and announcements', () => {
  const T = (minutes: number) => NOW + minutes * 60_000;
  const fresh = (weekly: number, at: number, extra: Partial<UsageCli> = {}) =>
    cli(weekly, 20, { fetchedAt: new Date(at).toISOString(), availability: { status: 'complete', observedAt: new Date(at).toISOString(), ttlSeconds: 600 }, ...extra });

  it('announces a warning once, not on every refresh or numeric tick', () => {
    const { state, spoken } = run([
      [snapshot([fresh(70, T(0))]), T(0)],
      [snapshot([fresh(81, T(1))]), T(1)],
      [snapshot([fresh(82.5, T(2))]), T(2)],
      [snapshot([fresh(83, T(3))]), T(3)],
    ]);
    expect(spoken).toEqual([[], ['Codex weekly quota 81 percent used, above 80 percent.'], [], []]);
    expect(Object.values(state.active)[0].label).toBe('Weekly 83% used');
  });

  it('does not re-announce a warning that flaps within the same reset cycle', () => {
    const { spoken } = run([
      [snapshot([fresh(81, T(0))]), T(0)],
      [snapshot([fresh(79, T(1))]), T(1)],
      [snapshot([fresh(81, T(2))]), T(2)],
    ]);
    expect(spoken.map(s => s.length)).toEqual([1, 0, 0]);
  });

  it('does not re-announce every refresh when the provider reports a reset instant in the past', () => {
    const pastReset = (at: number) => {
      const c = fresh(88, at);
      c.windows[0].resetAtUtc = '2026-09-25T10:00:00Z';
      return c;
    };
    const { spoken, state } = run([
      [snapshot([pastReset(T(0))]), T(0)],
      [snapshot([pastReset(T(1))]), T(1)],
      [snapshot([pastReset(T(2))]), T(2)],
    ]);
    expect(spoken.map(s => s.length)).toEqual([1, 0, 0]);
    expect(Object.values(state.active)[0].cycleEndsAt).toBeNull();
  });

  it('announces again in the next reset cycle', () => {
    const later = Date.parse(WEEKLY_RESET) + 60_000;
    const next = fresh(85, later);
    next.windows[0].resetAtUtc = '2026-10-06T07:00:00Z';
    const { spoken } = run([[snapshot([fresh(81, T(0))]), T(0)], [snapshot([next]), later]]);
    expect(spoken.map(s => s.length)).toEqual([1, 1]);
  });

  it('does not clear a quota warning at reset without a confirmed next window', () => {
    const { state } = run([[snapshot([fresh(90, T(0))]), T(0)]]);
    const later = Date.parse(WEEKLY_RESET) + 60_000;
    const stale = fresh(10, T(0), { availability: { status: 'stale', observedAt: new Date(T(0)).toISOString(), ttlSeconds: 600 } });
    const unconfirmed = run([[snapshot([stale]), later]], state);
    expect(Object.keys(unconfirmed.state.active)).toEqual(['cli:codex/quota/codex/weekly']);
    expect(unconfirmed.spoken).toEqual([[]]);

    const next = fresh(10, later);
    next.windows[0].resetAtUtc = '2026-10-06T07:00:00Z';
    const recovered = run([[snapshot([next]), later]], unconfirmed.state);
    expect(recovered.state.active).toEqual({});
    expect(recovered.spoken).toEqual([[]]);
  });

  it('keeps a quota warning through stale and suspicious readings, and clears it on trusted recovery', () => {
    const { state: kept, spoken } = run([
      [snapshot([fresh(90, T(0))]), T(0)],
      [snapshot([cli(10, 10, { ...STALE, fetchedAt: new Date(T(0)).toISOString() })]), T(30)],
      [snapshot([cli(10, 10, SUSPICIOUS)]), T(31)],
    ]);
    expect(Object.keys(kept.active)).toEqual(['cli:codex/quota/codex/weekly']);
    expect(spoken.map(s => s.length)).toEqual([1, 0, 0]);
    const recovered = run([[snapshot([fresh(40, T(32))]), T(32)]], kept);
    expect(recovered.state.active).toEqual({});
    expect(recovered.spoken).toEqual([[]]);
  });

  it('keeps a weekly warning when a fresh response omits that window', () => {
    const { state } = run([[snapshot([fresh(90, T(0))]), T(0)]]);
    const withoutWeekly = fresh(10, T(1));
    withoutWeekly.windows = withoutWeekly.windows.filter(w => w.id !== 'codex/weekly');
    const omitted = run([[snapshot([withoutWeekly]), T(1)]], state);
    expect(Object.keys(omitted.state.active)).toEqual(['cli:codex/quota/codex/weekly']);
    const recovered = run([[snapshot([fresh(40, T(2))]), T(2)]], omitted.state);
    expect(recovered.state.active).toEqual({});
  });

  it('never lets stale or unreadable data clear a limit; trusted recovery clears and is announced once', () => {
    const limited = cli(12, 5, { limited: true, limitedReason: 'Rate limit reached' });
    const { state, spoken } = run([
      [snapshot([limited]), T(0)],
      [snapshot([limited]), T(1)],
      [snapshot([cli(12, 5, { ...STALE, limited: false })]), T(2)],
      [snapshot([cli(12, 5, { limited: null })]), T(3)],
      [snapshot([]), T(4)],
    ]);
    expect(spoken).toEqual([['Codex is limited by the provider: Rate limit reached.'], [], [], [], []]);
    expect(Object.keys(state.active)).toEqual(['cli:codex/limited']);

    const recovered = run([[snapshot([cli(12, 5)]), T(5)], [snapshot([cli(12, 5)]), T(6)]], state);
    expect(recovered.state.active).toEqual({});
    expect(recovered.spoken).toEqual([['Codex is no longer limited.'], []]);

    const again = run([[snapshot([limited]), T(7)]], recovered.state);
    expect(again.spoken[0]).toHaveLength(1);
  });

  it('keeps a budget alarm past its cycle until a trusted next window confirms recovery', () => {
    const budgets = { dailyBudgetUsd: 50, weeklyBudgetUsd: null };
    const { state } = run([[snapshot([], cost(60, 60, budgets)), T(0)]]);
    expect(Object.keys(state.active)).toEqual(['cost/budget/daily']);
    const nextDay = Date.parse('2026-09-25T22:00:01Z');
    const stale = cost(60, 60, { ...budgets, coverage: { status: 'complete', observedAt: new Date(T(0)).toISOString(), ttlSeconds: 60 } });
    const after = run([[snapshot([], stale), nextDay]], state);
    expect(Object.keys(after.state.active)).toEqual(['cost/budget/daily']);
    expect(after.spoken).toEqual([[]]);
    const nextWindow = cost(10, 60, { ...budgets, coverage: {
      status: 'complete', observedAt: new Date(nextDay).toISOString(), ttlSeconds: 600,
    } });
    nextWindow.calendar.dayStartUtc = '2026-09-25T22:00:00Z';
    nextWindow.calendar.dayEndUtc = '2026-09-26T22:00:00Z';
    const recovered = run([[snapshot([], nextWindow), nextDay]], after.state);
    expect(recovered.state.active).toEqual({});
    expect(recovered.spoken).toEqual([[]]);
  });

  it('announces daily and weekly overruns once each', () => {
    const budgets = { dailyBudgetUsd: 50, weeklyBudgetUsd: 200 };
    const { spoken } = run([
      [snapshot([], cost(49, 190, budgets)), T(0)],
      [snapshot([], cost(51, 195, budgets)), T(1)],
      [snapshot([], cost(52, 201, budgets)), T(2)],
      [snapshot([], cost(53, 202, budgets)), T(3)],
    ]);
    expect(spoken).toEqual([
      [],
      ["Today's cost $51.00 USD exceeds the daily budget of $50.00 USD."],
      ["This week's cost $201.00 USD exceeds the weekly budget of $200.00 USD."],
      [],
    ]);
  });

  it('starts over when the workspace changes', () => {
    const limited = cli(12, 5, { limited: true });
    const { state } = run([[snapshot([limited], null, 'ws-1'), T(0)]]);
    const other = run([[snapshot([cli(12, 5, { limited: null })], null, 'ws-2'), T(1)]], state);
    expect(other.state.active).toEqual({});
  });
});

describe('hidden-provider alarms', () => {
  it('collects alarms of CLIs whose chip is hidden, limits first, without a number', () => {
    const claude = cli(12, 5, { cliId: 'claude', limited: true, limitedReason: 'Rate limit reached' });
    const gemini = { ...cli(91, 5), cliId: 'gemini' };
    gemini.windows = gemini.windows.map(w => ({ ...w, id: w.id.replace('codex', 'gemini') }));
    const { state } = run([[snapshot([cli(15, 32), claude, gemini]), NOW]]);
    const hidden = hiddenProviderAlarms(state, ['codex']);
    expect(hidden.map(a => a.source)).toEqual(['cli:claude', 'cli:gemini']);
    expect(hiddenProviderAlarms(state, ['codex', 'claude', 'gemini'])).toEqual([]);

    const primary = buildCliChipView('codex', cli(15, 32), ZONE, NOW, [], hidden);
    expect(primary.alarm).toBeNull();
    expect(primary.hiddenAlarm).toBe('limited');
    expect(primary.weekly.value).toBe('15%');
    expect(primary.ariaLabel).toContain('Also needs attention: Claude is limited by the provider: Rate limit reached. Gemini weekly quota 91 percent used, above 80 percent.');
  });

  it('ordinary headroom stays neutral', () => {
    const view = buildCliChipView('codex', cli(80, 80), ZONE, NOW, [], []);
    expect(view.alarm).toBeNull();
    expect(view.hiddenAlarm).toBeNull();
    expect(view.ariaLabel).not.toMatch(/Warning|Limited|attention/);
  });
});
