import type { UsageCli, UsageCockpitResponse, UsageCostProjection } from './models/usage-cockpit.model';
import { cliDisplayName, findWindow, formatUsdExact, formatUsedPct, windowSpokenName } from './usage-chip.util';

/**
 * Usage alarm transitions (HUC-S5, docs/header-usage-cockpit/index.html
 * "Chip states"). Pure policy: evaluate one cockpit snapshot, then fold it
 * into the latched alarm state.
 *
 * - Quota warns when a fresh, confirmed window is strictly above 80% used.
 *   80 is headroom, 80.1 warns, values above 100 are never clamped.
 * - An explicit provider limit is an alarm at any percentage and outranks
 *   every other state.
 * - Cost warns when the known daily or weekly USD total strictly exceeds the
 *   configured budget. No budget, no alarm. Budgets are read from the
 *   projection only; this module never invents a financial limit.
 * - Only a trusted sample clears an alarm. Stale, suspicious, partial or
 *   unreadable data keeps the last confirmed alarm; a limit is never cleared
 *   by an untrusted sample.
 * - Each alarm is announced once per source, window and reset cycle.
 *
 * Visibility only: nothing here changes routing, correctness floors or
 * model pins.
 */

/** Strict threshold: used percentages above this value warn. */
export const QUOTA_WARNING_ABOVE_PCT = 80;

const DEFAULT_TTL_SECONDS = 600;

export type UsageAlarmKind = 'limited' | 'quota' | 'budget';
export type UsageAlarmSeverity = 'critical' | 'warning';

export interface UsageAlarm {
  /** Source, kind and window identity: `cli:codex/quota/codex/weekly`. */
  key: string;
  /** `cli:<id>` or `cost`. */
  source: string;
  kind: UsageAlarmKind;
  severity: UsageAlarmSeverity;
  /** Quota window id, `daily` or `weekly` for budgets, `null` for a limit. */
  window: string | null;
  /**
   * End of the reset cycle in epoch ms: provider reset instant or the end of
   * the workspace-local day or week. `null` when unknown (limits).
   */
  cycleEndsAt: number | null;
  /** Reading order within a source: limit, then weekly or daily, then session or weekly budget. */
  order: number;
  /** Short wording for the expanded view, e.g. `Weekly 84.5% used`. */
  label: string;
  /** One complete sentence for the accessible name and the announcement. */
  sentence: string;
}

export interface UsageSourceObservation {
  source: string;
  /** Alarms confirmed by this sample. */
  alarms: UsageAlarm[];
  /** Kinds this sample is trusted to clear when no alarm of that kind is present. */
  clears: UsageAlarmKind[];
}

// ------------------------------------------------------------ evaluation

function epoch(iso: string | null | undefined): number | null {
  if (!iso) return null;
  const at = Date.parse(iso);
  return Number.isFinite(at) ? at : null;
}

function spokenPct(pct: string): string {
  return pct.replace('%', ' percent');
}

/**
 * A quota sample is trusted when it is present, complete, confirmed and
 * within its refresh interval. Stale, suspicious and unavailable samples
 * neither raise nor clear a quota alarm.
 */
export function isTrustedQuotaSample(cli: UsageCli, now: number): boolean {
  if (!cli.fetchedAt || cli.availability?.status !== 'complete') return false;
  if (cli.suspicious || cli.probeFailedAt) return false;
  if (cli.windows.some(w => !!w.suspiciousReason)) return false;
  const fetched = epoch(cli.fetchedAt);
  const ttl = cli.ttlSeconds > 0 ? cli.ttlSeconds : DEFAULT_TTL_SECONDS;
  return fetched != null && now - fetched <= ttl * 1000;
}

export function observeCli(cli: UsageCli, now: number): UsageSourceObservation {
  const source = `cli:${cli.cliId}`;
  const name = cliDisplayName(cli.cliId);
  const alarms: UsageAlarm[] = [];
  const clears: UsageAlarmKind[] = [];

  const trusted = isTrustedQuotaSample(cli, now);
  // `limited === null` means the limit source could not be read: no claim
  // either way. A limit clears only on a trusted sample that reports none.
  if (cli.limited === true) {
    const reason = cli.limitedReason?.trim();
    alarms.push({
      key: `${source}/limited`, source, kind: 'limited', severity: 'critical', window: null, cycleEndsAt: null, order: 0,
      label: reason ? `Limited: ${reason}` : 'Limited by the provider',
      sentence: `${name} is limited by the provider${reason ? `: ${reason.replace(/[.]$/, '')}` : ''}.`,
    });
  } else if (cli.limited === false && trusted) {
    clears.push('limited');
  }

  if (trusted) {
    clears.push('quota');
    for (const kind of ['weekly', 'session'] as const) {
      const w = findWindow(cli.windows, kind);
      if (w?.usedPct == null || !Number.isFinite(w.usedPct) || w.usedPct <= QUOTA_WARNING_ABOVE_PCT) continue;
      const pct = formatUsedPct(w.usedPct)!;
      // A reset instant already in the past is not a usable cycle end; treating
      // it as one would re-announce the same alarm on every refresh.
      const reset = epoch(w.resetAtUtc);
      alarms.push({
        key: `${source}/quota/${w.id}`, source, kind: 'quota', severity: 'warning', window: w.id,
        cycleEndsAt: reset != null && reset > now ? reset : null, order: kind === 'weekly' ? 1 : 2,
        label: `${w.label} ${pct} used`,
        sentence: `${name} ${windowSpokenName(w, kind)} quota ${spokenPct(pct)} used, above ${QUOTA_WARNING_ABOVE_PCT} percent.`,
      });
    }
  }
  return { source, alarms, clears };
}

type BudgetPeriod = 'daily' | 'weekly';

/**
 * Budget evaluation. A known total that strictly exceeds its budget warns,
 * including a partial total (it is a lower bound) and a stale total from the
 * current period (ledger totals only grow). Only complete, fresh coverage
 * clears a budget alarm.
 */
export function observeCost(cost: UsageCostProjection, now: number): UsageSourceObservation {
  const source = 'cost';
  const status = cost.coverage?.status;
  const observed = epoch(cost.coverage?.observedAt);
  const ttl = cost.coverage?.ttlSeconds && cost.coverage.ttlSeconds > 0 ? cost.coverage.ttlSeconds : DEFAULT_TTL_SECONDS;
  const fresh = observed != null && now - observed <= ttl * 1000;
  const alarms: UsageAlarm[] = [];
  if (status !== 'unavailable' && status !== 'suspicious') {
    const periods: [BudgetPeriod, number | null, number | null | undefined, string | undefined, string | undefined][] = [
      ['daily', cost.todayUsd, cost.dailyBudgetUsd, cost.calendar?.dayStartUtc, cost.calendar?.dayEndUtc],
      ['weekly', cost.weekUsd, cost.weeklyBudgetUsd, cost.calendar?.weekStartUtc, cost.calendar?.weekEndUtc],
    ];
    for (const [period, amount, budget, startIso, endIso] of periods) {
      const start = epoch(startIso);
      const end = epoch(endIso);
      if (amount == null || budget == null || !Number.isFinite(amount) || !Number.isFinite(budget)) continue;
      if (start == null || end == null || now < start || now >= end) continue;
      if (amount <= budget) continue;
      const spent = formatUsdExact(amount)!;
      const limit = formatUsdExact(budget)!;
      const scope = period === 'daily' ? "Today's" : "This week's";
      alarms.push({
        key: `${source}/budget/${period}`, source, kind: 'budget', severity: 'warning', window: period, cycleEndsAt: end,
        order: period === 'daily' ? 1 : 2,
        label: `${period === 'daily' ? 'Daily' : 'Weekly'} budget ${limit} exceeded: ${spent}`,
        sentence: `${scope} cost ${spent} USD exceeds the ${period} budget of ${limit} USD.`,
      });
    }
  }
  return { source, alarms, clears: status === 'complete' && fresh ? ['budget'] : [] };
}

/** Every source in one snapshot. */
export function observeSnapshot(snapshot: UsageCockpitResponse, now: number): UsageSourceObservation[] {
  return [
    ...snapshot.clis.map(cli => observeCli(cli, now)),
    ...(snapshot.cost ? [observeCost(snapshot.cost, now)] : []),
  ];
}

// --------------------------------------------------------------- reducer

export interface UsageAlarmState {
  /** Workspace the latched alarms belong to; a switch starts over. */
  workspaceId: string | null;
  /** Latched alarms by key. */
  active: Readonly<Record<string, UsageAlarm>>;
  /**
   * Announced keys with the cycle end they were announced for. A key is not
   * announced again until that cycle has ended (or, for a limit or an
   * unknown cycle end, until a trusted recovery).
   */
  announced: Readonly<Record<string, number | null>>;
}

export const EMPTY_USAGE_ALARM_STATE: UsageAlarmState = { workspaceId: null, active: {}, announced: {} };

export interface UsageAlarmStep {
  state: UsageAlarmState;
  /** Polite announcements for this step, in source order. Empty on a quiet refresh. */
  announcements: string[];
}

/**
 * Fold one set of observations into the latched state.
 *
 * - A quota or budget alarm expires quietly when its reset cycle ends; the
 *   chip then shows whatever the current data supports.
 * - A trusted sample without the alarm clears it. A cleared limit announces
 *   its recovery once; quota and budget recoveries stay quiet.
 * - An untrusted sample keeps every latched alarm of its source.
 */
export function reduceUsageAlarms(
  previous: UsageAlarmState,
  workspaceId: string | null,
  observations: readonly UsageSourceObservation[],
  now: number,
): UsageAlarmStep {
  const base = previous.workspaceId === workspaceId ? previous : { ...EMPTY_USAGE_ALARM_STATE, workspaceId };
  const active: Record<string, UsageAlarm> = {};
  const announced: Record<string, number | null> = {};
  for (const [key, alarm] of Object.entries(base.active)) {
    if (alarm.cycleEndsAt == null || now < alarm.cycleEndsAt) active[key] = alarm;
  }
  for (const [key, end] of Object.entries(base.announced)) {
    if (end == null || now < end) announced[key] = end;
  }
  const announcements: string[] = [];

  for (const observation of observations) {
    const raised = new Set(observation.alarms.map(a => a.key));
    for (const [key, alarm] of Object.entries(active)) {
      if (alarm.source !== observation.source || raised.has(key) || !observation.clears.includes(alarm.kind)) continue;
      delete active[key];
      if (alarm.kind === 'limited') {
        announcements.push(`${cliDisplayName(alarm.source.replace(/^cli:/, ''))} is no longer limited.`);
      }
      if (alarm.kind === 'limited' || announced[key] == null) delete announced[key];
    }
    for (const alarm of observation.alarms) {
      // Expired cycles were dropped above, so presence means "this cycle".
      if (!(alarm.key in announced)) {
        announcements.push(alarm.sentence);
        announced[alarm.key] = alarm.cycleEndsAt;
      }
      active[alarm.key] = alarm;
    }
  }
  return { state: { workspaceId, active, announced }, announcements };
}

// ------------------------------------------------------------- selectors

const byReadingOrder = (a: UsageAlarm, b: UsageAlarm) =>
  a.source.localeCompare(b.source) || a.order - b.order || a.key.localeCompare(b.key);

/** Latched alarms of one source in reading order: limit, weekly, session. */
export function alarmsForSource(state: UsageAlarmState, source: string): UsageAlarm[] {
  return Object.values(state.active)
    .filter(alarm => alarm.source === source)
    .sort(byReadingOrder);
}

/**
 * CLI alarms whose chip is not visible (phone, or secondary CLIs moved into
 * Details), by provider then reading order. The primary trigger shows them
 * as one nonnumeric mark.
 */
export function hiddenProviderAlarms(state: UsageAlarmState, visibleCliIds: readonly string[]): UsageAlarm[] {
  const visible = new Set(visibleCliIds.map(id => `cli:${id}`));
  return Object.values(state.active)
    .filter(alarm => alarm.source.startsWith('cli:') && !visible.has(alarm.source))
    .sort(byReadingOrder);
}
