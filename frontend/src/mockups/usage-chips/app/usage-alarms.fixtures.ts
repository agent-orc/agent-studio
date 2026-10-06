import {
  alarmsForSource,
  EMPTY_USAGE_ALARM_STATE,
  hiddenProviderAlarms,
  observeSnapshot,
  reduceUsageAlarms,
  type UsageAlarm,
  type UsageAlarmState,
  type UsageCli,
  type UsageCockpitResponse,
  type UsageCostProjection,
} from '../../../app/features/usage-cockpit';
import * as F from './usage-chips.fixtures';

/**
 * Synthetic HUC-S5 alarm fixtures, pinned to 25 September 2026 16:58
 * Europe/Berlin like the HUC-S2 fixtures. Every case is latched through the
 * real `reduceUsageAlarms`, so the gallery shows what the policy decides.
 * The budgets below are fixture values: no budget owner exists in the
 * product yet, and the live projection reports none.
 */
export const NOW = F.FIXTURE_NOW;
export const ZONE = F.FIXTURE_ZONE;
const at = (minutes: number) => NOW + minutes * 60_000;
const iso = (epochMs: number) => new Date(epochMs).toISOString();

export function snapshot(clis: UsageCli[], cost: UsageCostProjection | null = null): UsageCockpitResponse {
  return {
    snapshotVersion: 1, workspaceId: 'fixture-workspace', timeZone: ZONE, weekStart: 1, generatedAt: iso(NOW),
    calendar: F.COST.calendar, clis, cost: (cost ?? F.COST), runs: [], slots: [], sources: {},
  };
}

function latch(steps: [UsageCockpitResponse, number][]): UsageAlarmState {
  return steps.reduce(
    (state, [snap, when]) => reduceUsageAlarms(state, snap.workspaceId, observeSnapshot(snap, when), when).state,
    EMPTY_USAGE_ALARM_STATE,
  );
}

/** A CLI sample observed at `when`, with its weekly and session percentages. */
export function sample(base: UsageCli, weekly: number, session: number, when = at(-3), extra: Partial<UsageCli> = {}): UsageCli {
  const [w, s, ...rest] = base.windows;
  return {
    ...base,
    windows: [{ ...w, usedPct: weekly }, { ...s, usedPct: session }, ...rest],
    fetchedAt: iso(when),
    availability: { status: 'complete', observedAt: iso(when), ttlSeconds: 600 },
    ...extra,
  };
}

const staleProbe = (when: number): Partial<UsageCli> => ({
  probeFailedAt: iso(at(-8)),
  availability: { status: 'stale', observedAt: iso(when), ttlSeconds: 600 },
});

export interface CliCase { id: string; cli: UsageCli; alarms: UsageAlarm[]; hidden: UsageAlarm[] }
export interface CostCase { id: string; cost: UsageCostProjection; alarms: UsageAlarm[] }

/** Latch the steps; the case shows the last step's data and the latched alarms. */
function cliCase(id: string, steps: [UsageCli, number][], visible?: string[]): CliCase {
  const state = latch(steps.map(([c, when]) => [snapshot([c]), when]));
  const cli = steps[steps.length - 1][0];
  return { id, cli, alarms: alarmsForSource(state, `cli:${cli.cliId}`), hidden: visible ? hiddenProviderAlarms(state, visible) : [] };
}

function costCase(id: string, steps: [UsageCostProjection, number][]): CostCase {
  const state = latch(steps.map(([c, when]) => [snapshot([], c), when]));
  return { id, cost: steps[steps.length - 1][0], alarms: alarmsForSource(state, 'cost') };
}

const BUDGETS = { dailyBudgetUsd: 50, weeklyBudgetUsd: 200 };
function cost(todayUsd: number | null, weekUsd: number | null, extra: Partial<UsageCostProjection> = {}): UsageCostProjection {
  return { ...F.COST, ...BUDGETS, todayUsd, weekUsd, ...extra };
}
const coverage = (status: string, observedMinutes = -1, reason?: string) =>
  ({ status, observedAt: status === 'unavailable' ? null : iso(at(observedMinutes)), ttlSeconds: 60, reason });

// ------------------------------------------------------------ quota

export const QUOTA = [
  cliCase('codex-84', [[sample(F.CODEX, 84.5, 32), NOW]]),
  cliCase('claude-80', [[sample(F.CLAUDE, 7, 80), NOW]]),
  cliCase('codex-over-100', [[sample(F.CODEX, 104.25, 137), NOW]]),
];

// ------------------------------------------------------------- cost

export const COSTS = [
  costCase('daily-over', [[cost(52.3, 120), NOW]]),
  costCase('weekly-over', [[cost(31.2, 214.75), NOW]]),
  costCase('at-budget', [[cost(50, 200), NOW]]),
  costCase('no-budget', [[{ ...F.COST, todayUsd: 12480.37, weekUsd: 40210 }, NOW]]),
];

// ---------------------------------------------------------- limited

const CLAUDE_LIMITED = sample(F.CLAUDE, 3, 1, at(-3), { limited: true, limitedReason: 'Rate limit reached' });
export const LIMITED = [
  cliCase('claude-limited', [[CLAUDE_LIMITED, NOW]]),
  cliCase('codex-limited-over', [[sample(F.CODEX, 92, 40, at(-3), { limited: true, limitedReason: 'Admission paused' }), NOW]]),
];

// ------------------------------------------------------------ stale

export const STALE = [
  // A confirmed warning survives a stale sample; the values read as last-known.
  cliCase('codex-warning-retained', [
    [sample(F.CODEX, 90, 32, at(-22)), at(-20)],
    [sample(F.CODEX, 90, 32, at(-22), staleProbe(at(-22))), NOW],
  ]),
  // A stale sample reporting no limit cannot clear the limit.
  cliCase('claude-limit-retained', [
    [sample(F.CLAUDE, 3, 1, at(-22), { limited: true, limitedReason: 'Rate limit reached' }), at(-20)],
    [sample(F.CLAUDE, 3, 1, at(-22), { ...staleProbe(at(-22)), limited: false }), NOW],
  ]),
  // A stale 95% reading alone is not a confirmed warning.
  cliCase('codex-stale-high', [[sample(F.CODEX, 95, 40, at(-22), staleProbe(at(-22))), NOW]]),
];
export const COST_STALE = costCase('stale-budget', [[cost(20, 80, { coverage: coverage('complete', -38) }), NOW]]);

// ---------------------------------------------------------- partial

export const PARTIAL = [
  costCase('partial-under', [[cost(38.1, 150, { coverage: coverage('partial', -1, 'Some models have no historical USD price') }), NOW]]),
  costCase('partial-over', [[cost(61.4, 150, { coverage: coverage('partial', -1, 'Some models have no historical USD price') }), NOW]]),
];

// ---------------------------------------------------------- unknown

export const UNKNOWN_CLI = cliCase('gemini-unknown', [[{ ...F.GEMINI_UNAVAILABLE, limited: null }, NOW]]);
export const UNKNOWN_COST = costCase('cost-unknown', [[cost(null, null, { coverage: coverage('unavailable', 0, 'The token ledger could not be read') }), NOW]]);

// ------------------------------------------------------------ phone

const GEMINI_WARN: UsageCli = {
  ...sample(F.CODEX, 91, 20),
  cliId: 'gemini',
  windows: F.CODEX.windows.map((w, i) => ({ ...w, id: w.id.replace('codex', 'gemini'), usedPct: i === 0 ? 91 : 20 })),
};
/** Phone: only the primary CLI chip is visible; Claude and Gemini alarm out of sight. */
export const PHONE_PRIMARY = (() => {
  const state = latch([[snapshot([sample(F.CODEX, 15, 32), CLAUDE_LIMITED, GEMINI_WARN]), NOW]]);
  return {
    id: 'phone-primary', cli: sample(F.CODEX, 15, 32),
    alarms: alarmsForSource(state, 'cli:codex'), hidden: hiddenProviderAlarms(state, ['codex']),
  } satisfies CliCase;
})();

// ------------------------------------------------------ transitions

export interface TransitionStep { label: string; snapshot: UsageCockpitResponse; at: number }

/** Scripted refreshes for the live-region demo, 30 seconds apart. */
export const TRANSITIONS: TransitionStep[] = [
  ['Headroom 70%', sample(F.CODEX, 70, 30, at(0))],
  ['Crosses 80: 81%', sample(F.CODEX, 81, 31, at(0.5))],
  ['Refresh 82.5%', sample(F.CODEX, 82.5, 31, at(1))],
  ['Refresh 83%', sample(F.CODEX, 83, 32, at(1.5))],
  ['Provider limit at 83%', sample(F.CODEX, 83, 32, at(2), { limited: true, limitedReason: 'Rate limit reached' })],
  ['Stale probe says not limited', sample(F.CODEX, 83, 32, at(2), { ...staleProbe(at(2)), limited: false })],
  ['Refresh, still limited', sample(F.CODEX, 83, 32, at(3), { limited: true, limitedReason: 'Rate limit reached' })],
  ['Trusted recovery', sample(F.CODEX, 83, 32, at(3.5))],
].map(([label, cli], i) => ({ label: label as string, snapshot: snapshot([cli as UsageCli]), at: at(i * 0.5) }));

/** Ordinary today's cost for the phone row, $12.48 like the Dossier composition. */
export const PHONE_COST = F.COST;
