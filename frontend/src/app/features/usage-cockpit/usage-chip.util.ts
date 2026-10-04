import type {
  UsageCli,
  UsageCliWindow,
  UsageCostProjection,
  UsageSlotPool,
  UsageSourceState,
} from './models/usage-cockpit.model';
import type { UsageAlarm } from './usage-alarm.policy';

/**
 * Pure view models for the header usage chips (HUC-S2,
 * docs/header-usage-cockpit/index.html "Chip states").
 *
 * The rules that matter live here, not in templates:
 * - percentages mean quota used, keep at most one decimal and are never clamped;
 * - an unreported window or unknown cost renders `N/A`, never `0%` or `$0.00`;
 * - cost is the USD ledger estimate from the projection, never derived from quota;
 * - visible values may be shortened (`$12.5K`), the accessible name and the
 *   detail text always carry the exact amount.
 *
 * Alarm states (quota warning, provider limited, budget crossed) are derived
 * and latched by `usage-alarm.policy.ts` (HUC-S5); the builders here only
 * present the latched alarms they are given.
 */
export type UsageChipState = 'normal' | 'loading' | 'unknown' | 'stale' | 'suspicious' | 'partial';

/** Visible replacement for a value the source did not report. */
export const NOT_AVAILABLE = 'N/A';

export const DEFAULT_TTL_SECONDS = 600;
/** Visible cost values from this amount on use a compact unit ($12.5K). */
const COMPACT_USD_FROM = 10_000;

const PERCENT = new Intl.NumberFormat('en-US', { maximumFractionDigits: 1 });
const USD_EXACT = new Intl.NumberFormat('en-US', {
  style: 'currency', currency: 'USD', minimumFractionDigits: 2, maximumFractionDigits: 2,
});
const USD_COMPACT = new Intl.NumberFormat('en-US', {
  style: 'currency', currency: 'USD', notation: 'compact', maximumFractionDigits: 1,
});
const COUNT = new Intl.NumberFormat('en-US', { maximumFractionDigits: 0 });

// ---------------------------------------------------------------- numbers

/** `15`, `32.5`, `142` with a `%` sign; at most one decimal, never clamped. */
export function formatUsedPct(value: number | null | undefined): string | null {
  if (value == null || !Number.isFinite(value)) return null;
  return `${PERCENT.format(Math.round(value * 10) / 10)}%`;
}

/** Exact USD amount with cents: `$12,480.37`. */
export function formatUsdExact(value: number | null | undefined): string | null {
  if (value == null || !Number.isFinite(value)) return null;
  return USD_EXACT.format(value);
}

/**
 * Visible USD amount. Below $10,000 it is the exact amount; above, a complete
 * rounded amount with its unit (`$12.5K`, `$1.2M`) so digits are never cut.
 */
export function formatUsdCompact(value: number | null | undefined): string | null {
  if (value == null || !Number.isFinite(value)) return null;
  return Math.abs(value) >= COMPACT_USD_FROM ? USD_COMPACT.format(value) : USD_EXACT.format(value);
}

// ------------------------------------------------------------------ time

function validZone(timeZone: string | null | undefined): string {
  if (!timeZone) return 'UTC';
  try {
    new Intl.DateTimeFormat('en-GB', { timeZone });
    return timeZone;
  } catch {
    return 'UTC';
  }
}

function formatInZone(date: Date, timeZone: string): string {
  return new Intl.DateTimeFormat('en-GB', {
    timeZone, weekday: 'short', day: 'numeric', month: 'short',
    hour: '2-digit', minute: '2-digit', hourCycle: 'h23',
  }).format(date);
}

/**
 * Workspace-local time with its UTC equivalent (HUC-05):
 * `Mon 29 Sept, 09:00 Europe/Berlin (Mon 29 Sept, 07:00 UTC)`.
 */
export function formatLocalWithUtc(iso: string | null | undefined, timeZone: string | null | undefined): string | null {
  if (!iso) return null;
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return null;
  const zone = validZone(timeZone);
  const local = `${formatInZone(date, zone)} ${zone}`;
  return zone === 'UTC' ? local : `${local} (${formatInZone(date, 'UTC')} UTC)`;
}

// --------------------------------------------------------------- windows

export type UsageWindowKind = 'weekly' | 'session';

function isSecondaryModelWindow(label: string): boolean {
  // Codex reports separate Spark buckets next to the main ones; they are
  // model-level detail, not the CLI's weekly or session window.
  return label.includes('spark');
}

/** Provider wording for a five-hour window; decides both the session match and the `5H` tag. */
function isFiveHour(label: string): boolean {
  const l = label.toLowerCase();
  return l.includes('5h') || l.includes('5-hour') || l.includes('five-hour');
}

/** The provider window that plays the weekly or current-session role. */
export function findWindow(windows: readonly UsageCliWindow[], kind: UsageWindowKind): UsageCliWindow | null {
  const candidates = windows.filter(w => !isSecondaryModelWindow(w.label.toLowerCase()));
  if (kind === 'weekly') {
    const weekly = candidates.filter(w => w.label.toLowerCase().includes('week'));
    return weekly.find(w => w.label.toLowerCase().includes('all')) ?? weekly[0] ?? null;
  }
  return candidates.find(w => isFiveHour(w.label) || w.label.toLowerCase().includes('session')) ?? null;
}

export interface UsageWindowView {
  kind: UsageWindowKind;
  /** Short visible tag: `WK`, `5H` (only when the provider reports five hours) or `Session`. */
  tag: string;
  /** Visible value; `N/A` when the window is not reported. */
  value: string;
  reported: boolean;
  /** Spoken clause, e.g. `weekly 15 percent used`. */
  spoken: string;
  /** Detail line with the provider label, exact value and reset instant. */
  detail: string;
}

/** Spoken window name: `weekly`, `current five-hour window` or `current session`. */
export function windowSpokenName(window: UsageCliWindow | null, kind: UsageWindowKind): string {
  if (kind === 'weekly') return 'weekly';
  return window && isFiveHour(window.label) ? 'current five-hour window' : 'current session';
}

function windowView(window: UsageCliWindow | null, kind: UsageWindowKind, timeZone: string | null): UsageWindowView {
  const pct = formatUsedPct(window?.usedPct);
  const fiveHour = kind === 'session' && !!window && isFiveHour(window.label);
  const tag = kind === 'weekly' ? 'WK' : fiveHour ? '5H' : 'Session';
  const spokenName = windowSpokenName(window, kind);
  const providerLabel = window?.label ?? (kind === 'weekly' ? 'Weekly' : 'Current session');
  if (pct == null) {
    return {
      kind, tag, value: NOT_AVAILABLE, reported: false,
      spoken: `${spokenName} not reported`,
      detail: `${providerLabel}: not reported`,
    };
  }
  const reset = formatLocalWithUtc(window?.resetAtUtc, timeZone) ?? window?.resetLabel ?? null;
  return {
    kind, tag, value: pct, reported: true,
      spoken: `${spokenName} ${percentSpoken(pct)} used`,
    detail: `${providerLabel}: ${pct} used${reset ? `, resets ${reset}` : ''}`,
  };
}

// ---------------------------------------------------------------- states

export function ageExceedsTtl(observedAt: string | null | undefined, ttlSeconds: number | null | undefined, now: number): boolean {
  if (!observedAt) return false;
  const at = Date.parse(observedAt);
  if (!Number.isFinite(at)) return false;
  return now - at > (ttlSeconds && ttlSeconds > 0 ? ttlSeconds : DEFAULT_TTL_SECONDS) * 1000;
}

/** The same percent wording used by chip labels and alarm announcements. */
export function percentSpoken(pct: string): string {
  return pct.replace('%', ' percent');
}

/** Short visible and spoken wording per state; `null` for normal. */
export function stateWording(state: UsageChipState): string | null {
  switch (state) {
    case 'loading': return 'Loading';
    case 'unknown': return 'Unavailable';
    case 'stale': return 'Stale';
    case 'suspicious': return 'Unverified';
    case 'partial': return 'Partial';
    default: return null;
  }
}

function sentence(text: string): string {
  const trimmed = text.trim();
  return /[.!?]$/.test(trimmed) ? trimmed : `${trimmed}.`;
}

// ---------------------------------------------------------------- alarms

/** Alarm treatment of a chip. It outranks the data-quality `state`. */
export type UsageChipAlarm = 'limited' | 'warning';

function alarmTreatment(alarms: readonly UsageAlarm[]): UsageChipAlarm | null {
  if (alarms.some(a => a.severity === 'critical')) return 'limited';
  return alarms.length > 0 ? 'warning' : null;
}

/** Spoken alarm clause: `Limited: ... Warning: ...`, limit first. */
function alarmClause(alarms: readonly UsageAlarm[]): string {
  return alarms.map(a => `${a.severity === 'critical' ? 'Limited' : 'Warning'}: ${a.sentence}`).join(' ');
}

/** Explanation for alarms on CLIs whose chip is not visible. */
function hiddenClause(hidden: readonly UsageAlarm[]): string {
  return hidden.length ? `Also needs attention: ${hidden.map(a => a.sentence).join(' ')}` : '';
}

function join(...parts: string[]): string {
  return parts.filter(Boolean).join(' ');
}

// ------------------------------------------------------------- CLI chip

const CLI_NAMES: Record<string, string> = { claude: 'Claude', codex: 'Codex', gemini: 'Gemini' };

/** Display name of a CLI id; unknown ids keep their full provider name. */
export function cliDisplayName(cliId: string): string {
  return CLI_NAMES[cliId.toLowerCase()] ?? cliId;
}

export interface UsageCliChipView {
  cliId: string;
  name: string;
  state: UsageChipState;
  stateLabel: string | null;
  /** Latched alarm treatment; `null` for ordinary headroom. */
  alarm: UsageChipAlarm | null;
  /** Visible alarm word on the full chip; the compact chip shows only the mark. */
  alarmLabel: string | null;
  /** Worst alarm among CLIs whose chip is hidden; rendered as a nonnumeric mark. */
  hiddenAlarm: UsageChipAlarm | null;
  weekly: UsageWindowView;
  session: UsageWindowView;
  ariaLabel: string;
  detail: string;
}

/**
 * One CLI chip: weekly and current-session windows as one unit.
 * `cli === null` means the projection has not arrived yet. A CLI with no
 * reported value is unknown; otherwise the Dossier precedence applies:
 * suspicious, then stale, then normal ("Chip states").
 *
 * `alarms` are this CLI's latched HUC-S5 alarms and outrank the data state;
 * `hidden` are alarms of CLIs whose chip is not visible, announced on this
 * (primary) chip without another number.
 */
export function buildCliChipView(
  cliId: string,
  cli: UsageCli | null,
  timeZone: string | null,
  now: number,
  alarms: readonly UsageAlarm[] = [],
  hidden: readonly UsageAlarm[] = [],
  readFailed = false,
): UsageCliChipView {
  const name = cliDisplayName(cli?.cliId ?? cliId);
  const alarm = alarmTreatment(alarms);
  const alarmLabel = alarm === 'limited' ? 'Limited' : null;
  const hiddenAlarm = alarmTreatment(hidden);
  const alarmText = alarmClause(alarms);
  const hiddenText = hiddenClause(hidden);
  if (!cli) {
    const empty = (kind: UsageWindowKind) => ({ ...windowView(null, kind, timeZone), value: '' });
    return {
      cliId, name, state: readFailed ? 'unknown' : 'loading', stateLabel: readFailed ? 'Unavailable' : 'Loading', alarm, alarmLabel, hiddenAlarm,
      weekly: empty('weekly'), session: empty('session'),
      ariaLabel: join(`${name}, usage ${readFailed ? 'unavailable' : 'loading'}.`, readFailed ? 'The usage cockpit could not be loaded.' : '', alarmText, hiddenText, 'Open usage.'),
      detail: [`${name}: usage ${readFailed ? 'unavailable' : 'loading'}`, readFailed ? 'The usage cockpit could not be loaded.' : '', ...alarms.map(a => a.label), hiddenText].filter(Boolean).join('\n'),
    };
  }
  const weeklyWindow = findWindow(cli.windows, 'weekly');
  const sessionWindow = findWindow(cli.windows, 'session');
  const weekly = windowView(weeklyWindow, 'weekly', timeZone);
  const session = windowView(sessionWindow, 'session', timeZone);
  const status = cli.availability?.status;
  const suspiciousReason = cli.suspiciousReason
    ?? weeklyWindow?.suspiciousReason ?? sessionWindow?.suspiciousReason ?? null;
  const suspicious = cli.suspicious || status === 'suspicious'
    || !!weeklyWindow?.suspiciousReason || !!sessionWindow?.suspiciousReason;
  const stale = readFailed || status === 'stale' || !!cli.probeFailedAt
    || ageExceedsTtl(cli.fetchedAt, cli.ttlSeconds, now);
  const unknown = status === 'unavailable' || !cli.fetchedAt || (!weekly.reported && !session.reported);
  const state: UsageChipState = unknown ? 'unknown' : suspicious ? 'suspicious' : stale ? 'stale' : 'normal';

  const updated = formatLocalWithUtc(cli.fetchedAt, timeZone);
  const reason = state === 'unknown'
    ? cli.availability?.reason ?? 'The provider reported no usage window'
    : state === 'suspicious'
      ? suspiciousReason ?? 'The latest snapshot is not yet confirmed'
      : state === 'stale'
        ? readFailed ? 'The latest refresh failed; showing the last-known values' : cli.probeFailedAt ? 'The latest probe failed; showing the last good values' : 'The snapshot is older than its refresh interval'
        : null;
  const stateLabel = stateWording(state);
  const stateClause = stateLabel && reason ? `${stateLabel}: ${sentence(reason)}` : '';
  const spokenWindows = state === 'unknown'
    ? 'usage unavailable'
    : `${weekly.spoken}, ${session.spoken}`;
  const updatedClause = state === 'stale' && updated ? ` Last updated ${updated}.` : '';
  const ariaLabel = join(`${name}, ${spokenWindows}.`, alarmText, stateClause, updatedClause.trim(), hiddenText, 'Open usage.');
  // Simultaneous causes stay visible together in the detail.
  const detail = [
    cli.plan ? `${name} (${cli.plan})` : name,
    ...alarms.map(a => a.label),
    weekly.detail,
    session.detail,
    updated ? `Updated ${updated}` : 'Never updated',
    stateClause || null,
    hiddenText || null,
  ].filter(Boolean).join('\n');

  return { cliId, name, state, stateLabel, alarm, alarmLabel, hiddenAlarm, weekly, session, ariaLabel, detail };
}

// ------------------------------------------------------------ cost chip

export interface UsageCostChipView {
  state: UsageChipState;
  /** `warning` while a daily or weekly budget is exceeded; `null` otherwise. */
  alarm: UsageChipAlarm | null;
  /** Visible amount; `N/A` when unknown, empty while loading. */
  value: string;
  exact: string | null;
  ariaLabel: string;
  detail: string;
}

/**
 * Today's USD ledger estimate. The amount comes straight from the
 * projection; `todayUsd === null` is unknown and never shown as zero.
 *
 * `alarms` are the latched budget alarms. A configured budget without a
 * confirmed overrun is never reported as safe unless coverage is complete.
 */
export function buildCostChipView(
  cost: UsageCostProjection | null,
  now: number,
  alarms: readonly UsageAlarm[] = [],
  readFailed = false,
): UsageCostChipView {
  const alarm = alarmTreatment(alarms);
  const alarmText = alarmClause(alarms);
  if (!cost) {
    return {
      state: readFailed ? 'unknown' : 'loading', alarm, value: readFailed ? NOT_AVAILABLE : '', exact: null,
      ariaLabel: join(`Today's cost, ${readFailed ? 'unavailable' : 'loading'}.`, readFailed ? 'The usage cockpit could not be loaded.' : '', alarmText, 'Open cost detail.'),
      detail: [`Today's cost: ${readFailed ? 'unavailable' : 'loading'}`, readFailed ? 'The usage cockpit could not be loaded.' : '', ...alarms.map(a => a.label)].filter(Boolean).join('\n'),
    };
  }
  const zone = cost.calendar?.timeZone ?? null;
  const exact = formatUsdExact(cost.todayUsd);
  const coverage: UsageSourceState | undefined = cost.coverage;
  const status = coverage?.status;
  const unknown = exact == null || status === 'unavailable';
  const stale = readFailed || status === 'stale' || ageExceedsTtl(coverage?.observedAt, coverage?.ttlSeconds, now);
  const state: UsageChipState = unknown ? 'unknown'
    : status === 'suspicious' ? 'suspicious'
      : stale ? 'stale'
        : status === 'partial' ? 'partial'
          : 'normal';
  const reason = state === 'unknown'
    ? coverage?.reason ?? 'The token ledger is unavailable'
    : state === 'partial'
      ? coverage?.reason ?? 'Some usage is not priced or not yet received'
      : state === 'stale'
        ? readFailed ? 'The latest refresh failed; showing the last-known ledger total' : 'The ledger snapshot is older than its refresh interval'
        : state === 'suspicious' ? coverage?.reason ?? 'The ledger snapshot is not yet confirmed' : null;
  const stateLabel = stateWording(state);
  const stateClause = stateLabel && reason ? `${stateLabel}: ${sentence(reason)}` : '';
  const dayStart = formatLocalWithUtc(cost.calendar?.dayStartUtc, zone);
  const updated = formatLocalWithUtc(coverage?.observedAt, zone);
  const spokenValue = unknown ? 'unavailable' : `${exact} USD ledger estimate`;
  const budget = budgetClause(cost, state, alarms, now);
  return {
    state,
    alarm,
    value: unknown ? NOT_AVAILABLE : formatUsdCompact(cost.todayUsd) ?? NOT_AVAILABLE,
    exact: unknown ? null : exact,
    ariaLabel: join(`Today's cost, ${spokenValue}.`, alarmText, stateClause, budget, 'Open cost detail.'),
    detail: [
      unknown ? "Today's cost: unavailable" : `Today's cost: ${exact} USD, token-ledger estimate`,
      ...alarms.map(a => a.label),
      dayStart ? `Day starts ${dayStart}` : null,
      updated ? `Updated ${updated}` : null,
      stateClause || null,
      budget || null,
    ].filter(Boolean).join('\n'),
  };
}

/**
 * Budget wording beside the amount. Unset budgets say nothing (no alarm
 * without a budget). A configured budget without complete, fresh coverage
 * never reads as safe.
 */
export function hasCompleteBudgetCoverage(cost: UsageCostProjection, now: number): boolean {
  if (cost.coverage?.status !== 'complete' || !cost.coverage.observedAt
    || !Number.isFinite(Date.parse(cost.coverage.observedAt))
    || ageExceedsTtl(cost.coverage.observedAt, cost.coverage.ttlSeconds, now)) return false;
  for (const [budget, amount, startIso, endIso] of [
    [cost.dailyBudgetUsd, cost.todayUsd, cost.calendar?.dayStartUtc, cost.calendar?.dayEndUtc],
    [cost.weeklyBudgetUsd, cost.weekUsd, cost.calendar?.weekStartUtc, cost.calendar?.weekEndUtc],
  ] as const) {
    if (budget == null) continue;
    if (!Number.isFinite(budget) || amount == null || !Number.isFinite(amount)) return false;
    const start = Date.parse(startIso ?? '');
    const end = Date.parse(endIso ?? '');
    if (!Number.isFinite(start) || !Number.isFinite(end) || now < start || now >= end) return false;
  }
  return true;
}

function budgetClause(cost: UsageCostProjection, state: UsageChipState, alarms: readonly UsageAlarm[], now: number): string {
  const configured = cost.dailyBudgetUsd != null || cost.weeklyBudgetUsd != null;
  if (!configured) return '';
  if (state === 'normal' && hasCompleteBudgetCoverage(cost, now)) return alarms.length ? '' : 'Within budget.';
  return state === 'partial'
    ? 'Budget: totals are partial, an overrun may not be detected yet.'
    : 'Budget: not confirmed while the ledger is not current.';
}

// ------------------------------------------------------------ slot chip

export interface UsageSlotView {
  name: string;
  /** Visible `2/3`, or `N/A` when either counter is unknown. */
  value: string;
  spoken: string;
}

export interface UsageSlotChipView {
  state: UsageChipState;
  pools: UsageSlotView[];
  ariaLabel: string;
}

/**
 * Expanded-only slot chip. Remote, review and auto pools keep their own
 * occupancy and capacity; they are never summed.
 */
export function buildSlotChipView(slots: readonly UsageSlotPool[] | null): UsageSlotChipView {
  if (!slots) {
    return { state: 'loading', pools: [], ariaLabel: 'Slots, loading. Show slot pools.' };
  }
  const pools = slots.map(pool => {
    const known = pool.availability?.status !== 'unavailable' && pool.occupied != null && pool.capacity != null;
    return known
      ? {
          name: pool.name,
          value: `${COUNT.format(pool.occupied!)}/${COUNT.format(pool.capacity!)}`,
          spoken: `${pool.name} ${COUNT.format(pool.occupied!)} of ${COUNT.format(pool.capacity!)} in use`,
        }
      : { name: pool.name, value: NOT_AVAILABLE, spoken: `${pool.name} unavailable` };
  });
  const state: UsageChipState = pools.length === 0 || pools.every(p => p.value === NOT_AVAILABLE) ? 'unknown'
    : pools.some(p => p.value === NOT_AVAILABLE) ? 'partial' : 'normal';
  const spoken = pools.length === 0 ? 'no slot pools reported' : pools.map(p => p.spoken).join(', ');
  return { state, pools, ariaLabel: `Slots, ${spoken}. Show slot pools.` };
}
