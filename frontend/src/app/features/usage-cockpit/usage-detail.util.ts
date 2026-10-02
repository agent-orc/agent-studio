import type {
  UsageCalendar,
  UsageCli,
  UsageCliWindow,
  UsageCockpitResponse,
  UsageCostProjection,
  UsageRun,
  UsageSlotPool,
} from './models/usage-cockpit.model';
import {
  buildCliChipView,
  buildCostChipView,
  cliDisplayName,
  formatUsdExact,
  formatUsedPct,
  NOT_AVAILABLE,
  stateWording,
  type UsageChipState,
} from './usage-chip.util';

/**
 * Pure view models for the usage detail popover and sheet (HUC-S3,
 * docs/header-usage-cockpit/index.html "Popover and sheet").
 *
 * - Every provider window of a CLI is listed; reset instants carry the
 *   workspace-local time and the UTC time as two separate values.
 * - Project rows (including the unattributed bucket) reconcile with the
 *   displayed total. When rounding to cents breaks the sum, an explicit
 *   rounding row closes the gap; nothing is silently adjusted.
 * - Live runs are a breakdown of the totals, never an addition. A run without
 *   a receipt reads `Pending`, never `$0.00`.
 * - Effective model and reasoning come from each run. A missing value reads
 *   `Unknown`; it is never filled from a configured route or a global default.
 */

/** Visible label for a run's missing model, reasoning or CLI. */
export const UNKNOWN = 'Unknown';

/** Section the detail opens at. Every other section stays reachable. */
export type UsageDetailFocus = { kind: 'cli'; cliId: string } | { kind: 'cost' };

export interface UsageInstantView {
  /** `Mon 29 Sept, 09:00`, in the workspace zone. */
  local: string;
  /** `Mon 29 Sept, 07:00 UTC`. */
  utc: string;
  /** Machine-readable instant for `<time datetime>`. */
  iso: string;
}

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

/** One instant as workspace-local and UTC text (HUC-05); `null` for missing or invalid input. */
export function instantView(iso: string | null | undefined, timeZone: string | null | undefined): UsageInstantView | null {
  if (!iso) return null;
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return null;
  return {
    local: formatInZone(date, validZone(timeZone)),
    utc: `${formatInZone(date, 'UTC')} UTC`,
    iso: date.toISOString(),
  };
}

/** `in 3 h 32 min`, `in 4 d 16 h`, `in 45 s`; `due` once the instant has passed. */
export function formatCountdown(iso: string | null | undefined, now: number): string | null {
  if (!iso) return null;
  const at = Date.parse(iso);
  if (!Number.isFinite(at)) return null;
  const seconds = Math.floor((at - now) / 1000);
  if (seconds <= 0) return 'due';
  if (seconds < 60) return `in ${seconds} s`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `in ${minutes} min`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `in ${hours} h ${minutes % 60} min`;
  return `in ${Math.floor(hours / 24)} d ${hours % 24} h`;
}

// ---------------------------------------------------------- ledger links

/** Hash key-value segments that carry the ledger scope (see url-hash.util.ts). */
export const LEDGER_SCOPE_KEYS = {
  workspace: 'ledger-workspace',
  range: 'ledger-range',
  from: 'ledger-from',
  to: 'ledger-to',
  project: 'ledger-project',
  zone: 'ledger-zone',
} as const;

export type UsageLedgerRange = 'today' | 'week';

export interface UsageLedgerScope {
  workspaceId: string;
  range: UsageLedgerRange;
  /** Local-calendar boundary as a UTC instant, inclusive. */
  fromUtc: string;
  /** Local-calendar boundary as a UTC instant, exclusive. */
  toUtc: string;
  /** `null` for the whole workspace; `unattributed` for the unattributed bucket. */
  projectId?: string | null;
  /** Workspace IANA zone the range was computed in. */
  timeZone?: string | null;
}

/** Ledger project scope of the unattributed bucket. */
export const UNATTRIBUTED_KEY = 'unattributed';

/** CLI pages that already exist in the token-usage settings section. */
const LEDGER_CLI_PAGES = new Set(['claude', 'codex']);

/**
 * Hash link into the existing token-usage section of workspace settings. The
 * route segment picks the page; key-value segments preserve the workspace
 * and the local-calendar range as UTC instants.
 */
export function ledgerHref(scope: UsageLedgerScope, cliId: string | null = null): string {
  const page = cliId && LEDGER_CLI_PAGES.has(cliId.toLowerCase()) ? `/${cliId.toLowerCase()}` : '';
  const segments = [
    `/workspace/settings/tokens${page}`,
    `${LEDGER_SCOPE_KEYS.workspace}=${encodeURIComponent(scope.workspaceId)}`,
    `${LEDGER_SCOPE_KEYS.range}=${scope.range}`,
    `${LEDGER_SCOPE_KEYS.from}=${encodeURIComponent(scope.fromUtc)}`,
    `${LEDGER_SCOPE_KEYS.to}=${encodeURIComponent(scope.toUtc)}`,
  ];
  if (scope.timeZone) segments.push(`${LEDGER_SCOPE_KEYS.zone}=${encodeURIComponent(scope.timeZone)}`);
  if (scope.projectId) segments.push(`${LEDGER_SCOPE_KEYS.project}=${encodeURIComponent(scope.projectId)}`);
  return `#${segments.join('&')}`;
}

/** Reads a ledger scope from a hash; `null` unless workspace, range and both boundaries are present. */
export function ledgerScopeFromHash(hash: string): UsageLedgerScope | null {
  const values = new Map<string, string>();
  for (const segment of (hash || '').replace(/^#/, '').split('&')) {
    const eq = segment.indexOf('=');
    if (eq <= 0 || segment.startsWith('/')) continue;
    try {
      values.set(segment.slice(0, eq), decodeURIComponent(segment.slice(eq + 1)));
    } catch {
      return null;
    }
  }
  const workspaceId = values.get(LEDGER_SCOPE_KEYS.workspace);
  const range = values.get(LEDGER_SCOPE_KEYS.range);
  const fromUtc = values.get(LEDGER_SCOPE_KEYS.from);
  const toUtc = values.get(LEDGER_SCOPE_KEYS.to);
  if (!workspaceId || (range !== 'today' && range !== 'week') || !fromUtc || !toUtc) return null;
  if (!Number.isFinite(Date.parse(fromUtc)) || !Number.isFinite(Date.parse(toUtc))) return null;
  return {
    workspaceId, range, fromUtc, toUtc,
    projectId: values.get(LEDGER_SCOPE_KEYS.project) ?? null,
    timeZone: values.get(LEDGER_SCOPE_KEYS.zone) ?? null,
  };
}

/** Existing CLI management destination in workspace settings. */
export const CLI_MANAGEMENT_HREF = '#/workspace/settings/caps';

function rangeScope(workspaceId: string, calendar: UsageCalendar, range: UsageLedgerRange, projectId: string | null = null): UsageLedgerScope {
  const timeZone = calendar.timeZone;
  return range === 'today'
    ? { workspaceId, range, fromUtc: calendar.dayStartUtc, toUtc: calendar.dayEndUtc, projectId, timeZone }
    : { workspaceId, range, fromUtc: calendar.weekStartUtc, toUtc: calendar.weekEndUtc, projectId, timeZone };
}

/** One-line description of a ledger scope for the destination page. */
export function describeLedgerScope(scope: UsageLedgerScope): string {
  const zone = validZone(scope.timeZone);
  const from = instantView(scope.fromUtc, zone)!;
  const to = instantView(scope.toUtc, zone)!;
  const range = scope.range === 'today' ? 'Today' : 'This week';
  const project = scope.projectId === UNATTRIBUTED_KEY ? ', unattributed spend'
    : scope.projectId ? `, project ${scope.projectId}` : '';
  return `${range}, workspace ${scope.workspaceId}${project}: ${from.local} to ${to.local} ${zone} `
    + `(${from.utc} to ${to.utc})`;
}

// ---------------------------------------------------------- CLI section

export interface UsageDetailWindowView {
  id: string;
  label: string;
  /** Exact used percentage, or `Not reported`. */
  value: string;
  reported: boolean;
  reset: UsageInstantView | null;
  /** Provider wording when no reset instant is reported. */
  resetLabel: string | null;
  countdown: string | null;
  suspiciousReason: string | null;
}

export interface UsageRunRouteView {
  id: string;
  taskKey: string;
  projectName: string;
  started: UsageInstantView | null;
  /** `Configured codex / gpt-5 / high` when the route differs from the run; otherwise `null`. */
  configuredDifference: string | null;
  fallbackReason: string | null;
}

export interface UsageModelGroupView {
  key: string;
  model: string;
  reasoning: string;
  modelKnown: boolean;
  reasoningKnown: boolean;
  runs: UsageRunRouteView[];
}

export interface UsageCliDetailView {
  cliId: string;
  name: string;
  plan: string | null;
  source: string | null;
  primary: boolean;
  state: UsageChipState;
  stateLabel: string | null;
  stateReason: string | null;
  updated: UsageInstantView | null;
  windows: UsageDetailWindowView[];
  modelGroups: UsageModelGroupView[];
  ledgerHref: string | null;
}

function windowDetail(window: UsageCliWindow, timeZone: string, now: number): UsageDetailWindowView {
  const pct = formatUsedPct(window.usedPct);
  return {
    id: window.id,
    label: window.label,
    value: pct ? `${pct} used` : 'Not reported',
    reported: pct != null,
    reset: instantView(window.resetAtUtc, timeZone),
    resetLabel: window.resetAtUtc ? null : window.resetLabel,
    countdown: formatCountdown(window.resetAtUtc, now),
    suspiciousReason: window.suspiciousReason,
  };
}

function known(value: string | null | undefined): value is string {
  return !!value && value.trim().length > 0 && value.trim().toLowerCase() !== 'unknown';
}

function sameText(a: string | null | undefined, b: string | null | undefined): boolean {
  return (a ?? '').trim().toLowerCase() === (b ?? '').trim().toLowerCase();
}

function runRoute(run: UsageRun, projectNames: Map<string, string>, timeZone: string): UsageRunRouteView {
  // A configured value only counts as a difference when both sides are known;
  // an unknown effective value is never "explained" by the configured route.
  const differs = (configured: string, effective: string) =>
    known(configured) && (!known(effective) || !sameText(configured, effective));
  const anyDifference = differs(run.configuredCli, run.effectiveCli)
    || differs(run.configuredModel, run.effectiveModel)
    || differs(run.configuredReasoning, run.effectiveReasoning);
  const configured = [run.configuredCli, run.configuredModel, run.configuredReasoning]
    .map(v => (known(v) ? v : UNKNOWN)).join(' / ');
  return {
    id: run.id,
    taskKey: run.taskKey ?? run.taskId,
    projectName: projectNames.get(run.projectId) ?? run.projectId,
    started: instantView(run.startedAt, timeZone),
    configuredDifference: anyDifference ? `Configured ${configured}` : null,
    fallbackReason: run.fallbackReason,
  };
}

/** Runs grouped by effective model and reasoning, in stable order of first appearance by model name. */
export function groupRunsByModel(runs: readonly UsageRun[], projectNames: Map<string, string>, timeZone: string): UsageModelGroupView[] {
  const groups = new Map<string, UsageModelGroupView>();
  for (const run of runs) {
    const modelKnown = known(run.effectiveModel);
    const reasoningKnown = known(run.effectiveReasoning);
    const model = modelKnown ? run.effectiveModel.trim() : UNKNOWN;
    const reasoning = reasoningKnown ? run.effectiveReasoning.trim() : UNKNOWN;
    const key = `${model.toLowerCase()}\u0000${reasoning.toLowerCase()}`;
    let group = groups.get(key);
    if (!group) {
      group = { key, model, reasoning, modelKnown, reasoningKnown, runs: [] };
      groups.set(key, group);
    }
    group.runs.push(runRoute(run, projectNames, timeZone));
  }
  // Known models alphabetically, unknown last, so the order never depends on poll timing.
  return [...groups.values()].sort((a, b) =>
    Number(!a.modelKnown) - Number(!b.modelKnown)
    || a.model.localeCompare(b.model)
    || Number(!a.reasoningKnown) - Number(!b.reasoningKnown)
    || a.reasoning.localeCompare(b.reasoning));
}

function runsForCli(runs: readonly UsageRun[], cliId: string): UsageRun[] {
  return runs.filter(r => sameText(r.effectiveCli, cliId));
}

export function buildCliDetailView(
  cli: UsageCli,
  snapshot: UsageCockpitResponse,
  now: number,
): UsageCliDetailView {
  const zone = validZone(snapshot.timeZone);
  const chip = buildCliChipView(cli.cliId, cli, zone, now);
  const reasonLine = chip.detail.split('\n').find(line => chip.stateLabel != null && line.startsWith(`${chip.stateLabel}:`));
  const projectNames = projectNameMap(snapshot.cost);
  const calendar = snapshot.cost?.calendar ?? snapshot.calendar;
  return {
    cliId: cli.cliId,
    name: chip.name,
    plan: cli.plan,
    source: cli.source,
    primary: cli.primary,
    state: chip.state,
    stateLabel: chip.stateLabel,
    stateReason: reasonLine ? reasonLine.slice(chip.stateLabel!.length + 1).trim() : null,
    updated: instantView(cli.fetchedAt, zone),
    windows: cli.windows.map(w => windowDetail(w, zone, now)),
    modelGroups: groupRunsByModel(runsForCli(snapshot.runs, cli.cliId), projectNames, zone),
    ledgerHref: calendar ? ledgerHref(rangeScope(snapshot.workspaceId, calendar, 'week'), cli.cliId) : null,
  };
}

// --------------------------------------------------------- cost section

export interface UsageProjectRowView {
  key: string;
  name: string;
  unattributed: boolean;
  today: string;
  week: string;
  /** Exact amounts for the accessible row text. */
  todayExact: string;
  weekExact: string;
  coverageLabel: string | null;
  todayHref: string;
}

export interface UsageReconcileView {
  /** Visible `Rounding` row amounts, or `null` when the rows already sum to the total. */
  todayRemainder: string | null;
  weekRemainder: string | null;
  /** True when every project row and the total are known. */
  complete: boolean;
}

export interface UsageLiveRunView {
  id: string;
  taskKey: string;
  projectName: string;
  cliName: string;
  started: UsageInstantView | null;
  /** Provisional spend, or `Pending`. */
  spend: string;
  pending: boolean;
  /** `Included in today's total` or `Not yet in totals`. */
  inclusion: string;
  included: boolean;
}

export interface UsageSlotDetailView {
  name: string;
  value: string;
  meaning: string;
  status: string | null;
}

export interface UsageCostDetailView {
  state: UsageChipState;
  stateLabel: string | null;
  coverageReason: string | null;
  today: string;
  todayExact: string | null;
  week: string;
  weekExact: string | null;
  budgetDaily: string | null;
  budgetDailyRemaining: string | null;
  budgetWeekly: string | null;
  budgetWeeklyRemaining: string | null;
  dayStart: UsageInstantView | null;
  dayEnd: UsageInstantView | null;
  weekStart: UsageInstantView | null;
  weekEnd: UsageInstantView | null;
  timeZone: string;
  latestReceipt: UsageInstantView | null;
  pricingVersion: string;
  projects: UsageProjectRowView[];
  reconcile: UsageReconcileView;
  liveRuns: UsageLiveRunView[];
  slots: UsageSlotDetailView[];
  todayLedgerHref: string;
  weekLedgerHref: string;
}

function projectNameMap(cost: UsageCostProjection | null | undefined): Map<string, string> {
  return new Map((cost?.projects ?? []).filter(p => p.projectId).map(p => [p.projectId!, p.name]));
}

function cents(value: number): number {
  return Math.round(value * 100);
}

function centsText(value: number): string {
  return formatUsdExact(value / 100) ?? NOT_AVAILABLE;
}

/**
 * Rounding remainder that makes the cent-rounded rows sum to the cent-rounded
 * total. `null` when they already agree or when any amount is unknown.
 */
export function roundingRemainder(total: number | null, rows: readonly (number | null)[]): number | null {
  if (total == null || rows.some(r => r == null)) return null;
  const diff = cents(total) - rows.reduce<number>((sum, r) => sum + cents(r!), 0);
  return diff === 0 ? null : diff / 100;
}

const SLOT_MEANINGS: Record<string, string> = {
  remote: 'Runs executing on remote runner hosts',
  review: 'Remote review runs; counted separately from active runs',
  auto: 'Automatic pickup capacity across enabled hosts',
};

function slotDetail(pool: UsageSlotPool): UsageSlotDetailView {
  const knownCounts = pool.availability?.status !== 'unavailable' && pool.occupied != null && pool.capacity != null;
  return {
    name: pool.name,
    value: knownCounts ? `${pool.occupied} of ${pool.capacity} in use` : 'Unavailable',
    meaning: SLOT_MEANINGS[pool.name] ?? 'Slot pool reported by the workspace',
    status: pool.availability?.reason ?? null,
  };
}

function remaining(budget: number | null | undefined, spent: number | null): string | null {
  if (budget == null || spent == null) return null;
  const left = budget - spent;
  return left >= 0 ? `${formatUsdExact(left)} remaining` : `${formatUsdExact(-left)} over`;
}

export function buildCostDetailView(snapshot: UsageCockpitResponse, now: number): UsageCostDetailView {
  const cost = snapshot.cost;
  const zone = validZone(cost.calendar?.timeZone ?? snapshot.timeZone);
  const calendar = cost.calendar ?? snapshot.calendar;
  const chip = buildCostChipView(cost, now);
  const reasonLine = chip.detail.split('\n').find(line => chip.state !== 'normal' && line.startsWith(`${stateWording(chip.state)}:`));
  const projectNames = projectNameMap(cost);

  // Named projects by today's amount, then name; the unattributed bucket is always last.
  const ordered = [...cost.projects].sort((a, b) =>
    Number(a.projectId == null) - Number(b.projectId == null)
    || (b.todayUsd ?? -1) - (a.todayUsd ?? -1)
    || a.name.localeCompare(b.name));
  const projects = ordered.map<UsageProjectRowView>(p => {
    const unattributed = p.projectId == null;
    const status = p.coverage?.status;
    return {
      key: p.projectId ?? UNATTRIBUTED_KEY,
      name: unattributed ? 'Unattributed' : p.name,
      unattributed,
      today: formatUsdExact(p.todayUsd) ?? NOT_AVAILABLE,
      week: formatUsdExact(p.weekUsd) ?? NOT_AVAILABLE,
      todayExact: formatUsdExact(p.todayUsd) ?? 'unavailable',
      weekExact: formatUsdExact(p.weekUsd) ?? 'unavailable',
      coverageLabel: status && status !== 'complete' ? stateWording(status as UsageChipState) ?? status : null,
      todayHref: ledgerHref(rangeScope(snapshot.workspaceId, calendar, 'today', p.projectId ?? UNATTRIBUTED_KEY)),
    };
  });

  const todayRows = cost.projects.map(p => p.todayUsd);
  const weekRows = cost.projects.map(p => p.weekUsd);
  const todayRemainder = roundingRemainder(cost.todayUsd, todayRows);
  const weekRemainder = roundingRemainder(cost.weekUsd, weekRows);
  const complete = cost.todayUsd != null && cost.weekUsd != null
    && todayRows.every(v => v != null) && weekRows.every(v => v != null);

  const liveRuns = snapshot.runs.map<UsageLiveRunView>(run => {
    const spend = formatUsdExact(run.provisionalCostUsd);
    return {
      id: run.id,
      taskKey: run.taskKey ?? run.taskId,
      projectName: projectNames.get(run.projectId) ?? run.projectId,
      cliName: known(run.effectiveCli) ? cliDisplayName(run.effectiveCli) : UNKNOWN,
      started: instantView(run.startedAt, zone),
      spend: spend ?? 'Pending',
      pending: spend == null,
      inclusion: run.includedInTotals ? "Included in today's total" : 'Not yet in totals',
      included: run.includedInTotals,
    };
  });

  const usd = (v: number | null | undefined) => formatUsdExact(v);
  return {
    state: chip.state,
    stateLabel: stateWording(chip.state),
    coverageReason: reasonLine ? reasonLine.slice(reasonLine.indexOf(':') + 1).trim() : null,
    today: usd(cost.todayUsd) ?? NOT_AVAILABLE,
    todayExact: usd(cost.todayUsd),
    week: usd(cost.weekUsd) ?? NOT_AVAILABLE,
    weekExact: usd(cost.weekUsd),
    budgetDaily: usd(cost.dailyBudgetUsd),
    budgetDailyRemaining: remaining(cost.dailyBudgetUsd, cost.todayUsd),
    budgetWeekly: usd(cost.weeklyBudgetUsd),
    budgetWeeklyRemaining: remaining(cost.weeklyBudgetUsd, cost.weekUsd),
    dayStart: instantView(calendar.dayStartUtc, zone),
    dayEnd: instantView(calendar.dayEndUtc, zone),
    weekStart: instantView(calendar.weekStartUtc, zone),
    weekEnd: instantView(calendar.weekEndUtc, zone),
    timeZone: zone,
    latestReceipt: instantView(cost.latestReceiptAt ?? cost.coverage?.observedAt, zone),
    pricingVersion: cost.pricingVersion,
    projects,
    reconcile: {
      todayRemainder: todayRemainder == null ? null : centsText(cents(todayRemainder)),
      weekRemainder: weekRemainder == null ? null : centsText(cents(weekRemainder)),
      complete,
    },
    liveRuns,
    slots: snapshot.slots.map(slotDetail),
    todayLedgerHref: ledgerHref(rangeScope(snapshot.workspaceId, calendar, 'today')),
    weekLedgerHref: ledgerHref(rangeScope(snapshot.workspaceId, calendar, 'week')),
  };
}

// ----------------------------------------------------------- whole view

export interface UsageDetailView {
  workspaceId: string;
  timeZone: string;
  clis: UsageCliDetailView[];
  /** CLIs whose runs report no effective CLI; listed so they are never dropped. */
  unknownCliGroups: UsageModelGroupView[];
  cost: UsageCostDetailView;
}

export function buildUsageDetailView(snapshot: UsageCockpitResponse, now: number): UsageDetailView {
  const zone = validZone(snapshot.timeZone);
  const cliIds = new Set(snapshot.clis.map(c => c.cliId.toLowerCase()));
  const orphanRuns = snapshot.runs.filter(r => !known(r.effectiveCli) || !cliIds.has(r.effectiveCli.trim().toLowerCase()));
  // Primary CLI first, then the provider order of the projection.
  const clis = [...snapshot.clis].sort((a, b) => Number(b.primary) - Number(a.primary));
  return {
    workspaceId: snapshot.workspaceId,
    timeZone: zone,
    clis: clis.map(c => buildCliDetailView(c, snapshot, now)),
    unknownCliGroups: groupRunsByModel(orphanRuns, projectNameMap(snapshot.cost), zone),
    cost: buildCostDetailView(snapshot, now),
  };
}

/** DOM id of the section a focus opens at. */
export function detailSectionId(prefix: string, focus: UsageDetailFocus): string {
  return focus.kind === 'cost' ? `${prefix}-cost` : `${prefix}-cli-${focus.cliId.toLowerCase()}`;
}
