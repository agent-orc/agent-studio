import { TaskState } from '../../../models/task.model';
import { laneName } from '../../../models/lane-presentation';
import type { GateEconomicsRow } from '../../task-pipeline/models/gate-economics.model';
import type { TaskCycleTimeRow } from '../../project-cycle-time/models/project-cycle-time.model';
import {
  FLOW_FIGURE_TARGETS,
  FLOW_STAGE_WAIT_TARGET_MINUTES,
  FLOW_TREND_STEADY_SHARE,
  type FlowFigureKey,
  type FlowFigureUnit,
  type FlowStageKey,
  type FlowTarget,
} from '../models/flow-health-targets';
import {
  ALL_PROJECTS_ROW,
  FLOW_WINDOW_DAYS,
  type FlowBlock,
  type FlowBrake,
  type FlowFigure,
  type FlowHealthResponses,
  type FlowHealthSources,
  type FlowHealthView,
  type FlowSlots,
  type FlowStageWait,
  type FlowStatus,
  type FlowTrend,
  type FlowWindow,
} from '../models/flow-health.model';

const FIGURE_LABELS: Readonly<Record<FlowFigureKey, string>> = {
  batchesPerDay: 'Batches closed per day',
  meanBatchSize: 'Mean batch size',
  soloShare: 'Merged cards gated alone',
  fullSuiteRunsPerCard: 'Full suite runs per merged card',
  gateMinutesPerCard: 'Gate minutes per merged card',
  gateSlotsBusy: 'Gate slots busy now',
  reuseRate: 'Gate verdict reuse',
  reviewRoundsPerCard: 'Review rounds per card',
  reviewMinutesPerCard: 'Review minutes per card',
  reviewMinutesPerAspect: 'Review minutes per card',
  tokensPerCard: 'Tokens per delivered card',
  costPerCard: 'Cost per delivered card',
  backwardShare: 'Cards that moved backwards',
};

const STAGE_LABELS: Readonly<Record<FlowStageKey, string>> = {
  pickup: 'Pickup',
  review: 'Review',
  gate: 'Gate',
  decision: laneName(TaskState.HumanReview),
};

const STAGE_LANES: Readonly<Record<FlowStageKey, string | null>> = {
  pickup: TaskState.Ready,
  review: TaskState.AutoReview,
  gate: null,
  decision: TaskState.HumanReview,
};

const SLOT_NOUNS: Readonly<Record<FlowStageKey, string>> = {
  pickup: 'coding', review: 'review', gate: 'gate', decision: '',
};

const NUMBER = new Intl.NumberFormat('en-US', { maximumFractionDigits: 1 });
const COMPACT = new Intl.NumberFormat('en-US', { notation: 'compact', maximumFractionDigits: 1 });
const USD = new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD', maximumFractionDigits: 2 });

/** One figure value in its unit, for the table and the brake sentence. */
export function formatFigure(value: number | null, unit: FlowFigureUnit): string {
  if (value == null || !Number.isFinite(value)) return 'Unknown';
  switch (unit) {
    case 'perDay': return `${NUMBER.format(value)} / day`;
    case 'cards': return `${NUMBER.format(value)} cards`;
    case 'share': return `${Math.round(value * 100)}%`;
    case 'minutes': return `${NUMBER.format(value)} min`;
    case 'tokens': return COMPACT.format(value);
    case 'usd': return USD.format(value);
    default: return NUMBER.format(value);
  }
}

/** The stated target, for example `≤ 10 min` or `≥ 2 cards`. */
export function formatTarget(target: FlowTarget): string {
  return `${target.direction === 'max' ? '≤' : '≥'} ${formatFigure(target.value, target.unit)}`;
}

/** A wait age in the coarsest readable unit: minutes, hours, then days. */
export function formatAge(minutes: number | null): string {
  if (minutes == null || !Number.isFinite(minutes)) return 'unknown';
  if (minutes < 60) return `${Math.max(0, Math.round(minutes))} min`;
  if (minutes < 48 * 60) return `${Math.round(minutes / 60)} h`;
  return `${Math.round(minutes / 1440)} d`;
}

export function judge(value: number | null, target: FlowTarget): FlowStatus {
  if (value == null || !Number.isFinite(value)) return 'unknown';
  return (target.direction === 'max' ? value <= target.value : value >= target.value) ? 'met' : 'missed';
}

/** The 7-day value against the 30-day value, read in the target's direction. */
export function trend(value7d: number | null, value30d: number | null, target: FlowTarget): FlowTrend {
  if (value7d == null || value30d == null) return 'unknown';
  const base = Math.abs(value30d);
  const change = base === 0 ? (value7d === 0 ? 0 : Math.sign(value7d)) : (value7d - value30d) / base;
  if (Math.abs(change) < FLOW_TREND_STEADY_SHARE) return 'steady';
  return (change < 0) === (target.direction === 'max') ? 'better' : 'worse';
}

function figure(key: FlowFigureKey, value7d: number | null, value30d: number | null,
  extra: Partial<FlowFigure> = {}): FlowFigure {
  const target = FLOW_FIGURE_TARGETS[key];
  return {
    id: key, key, label: FIGURE_LABELS[key], unit: target.unit, target, value7d, value30d,
    status: judge(value7d, target), trend: trend(value7d, value30d, target), ...extra,
  };
}

function mean(values: readonly number[]): number | null {
  return values.length === 0 ? null : values.reduce((sum, value) => sum + value, 0) / values.length;
}

function ratio(numerator: number | null | undefined, denominator: number | null | undefined): number | null {
  return numerator == null || !denominator ? null : numerator / denominator;
}

// ---- grouping ----

/** Null without a merged card: an idle window has nothing to group. */
function batchesPerDay(row: GateEconomicsRow | null, window: FlowWindow): number | null {
  return row?.batchesClosed == null || !row.gatedMerges ? null : row.batchesClosed / FLOW_WINDOW_DAYS[window];
}

function soloShare(row: GateEconomicsRow | null): number | null {
  return row?.soloGatedMerges == null ? null : ratio(row.soloGatedMerges, row.gatedMerges);
}

function groupingBlock(sources: FlowHealthSources): FlowBlock {
  const { '7d': week, '30d': month } = sources.windows;
  const off = sources.batchingEnabled === false;
  const offNote = 'The batch gate is off for this project.';
  const grouping = (key: FlowFigureKey, read: (row: GateEconomicsRow | null, window: FlowWindow) => number | null) => {
    const value = figure(key, read(week.economics, '7d'), read(month.economics, '30d'),
      { all7d: read(week.economicsAll, '7d') });
    return off ? { ...value, status: 'not-applicable' as const, note: offNote } : value;
  };
  return {
    key: 'grouping',
    title: 'Grouping',
    figures: [
      grouping('batchesPerDay', batchesPerDay),
      grouping('meanBatchSize', row => row?.meanBatchSize ?? null),
      grouping('soloShare', soloShare),
    ],
  };
}

// ---- gate ----

function fullSuitePerCard(row: GateEconomicsRow | null): number | null {
  return ratio(row?.baseline?.fullSuiteRuns, row?.gatedMerges);
}

function reuse(row: GateEconomicsRow | null): number | null {
  if (row?.exactReuseRate == null && row?.disjointReuseRate == null) return null;
  return (row.exactReuseRate ?? 0) + (row.disjointReuseRate ?? 0);
}

function slotShare(slots: FlowSlots | null): number | null {
  return slots?.busy == null ? null : ratio(slots.busy, slots.total);
}

function gateBlock(sources: FlowHealthSources): FlowBlock {
  const { '7d': week, '30d': month } = sources.windows;
  const both = (key: FlowFigureKey, read: (row: GateEconomicsRow | null) => number | null) =>
    figure(key, read(week.economics), read(month.economics), { all7d: read(week.economicsAll) });
  const slots = sources.gateSlots;
  return {
    key: 'gate',
    title: 'Gate',
    figures: [
      both('fullSuiteRunsPerCard', fullSuitePerCard),
      both('gateMinutesPerCard', row => row?.baseline?.gateMinutesPerCardMedian ?? null),
      figure('gateSlotsBusy', slotShare(slots), null, {
        note: slots?.busy != null && slots.total != null
          ? `${slots.busy} of ${slots.total} slots busy; a live figure without a window.`
          : 'Gate slot use is not reported.',
      }),
      both('reuseRate', reuse),
    ],
  };
}

// ---- review ----

/** Cards whose stages were measured; backfilled rows date a completion only. */
function measured(rows: readonly TaskCycleTimeRow[] | undefined): TaskCycleTimeRow[] {
  return (rows ?? []).filter(row => row.stages != null);
}

function aspectLabel(step: string): string {
  const words = step.replace(/^aspect-/, '').replace(/-/g, ' ').trim();
  return words ? words.charAt(0).toUpperCase() + words.slice(1) : step;
}

/** Mean minutes per card that ran the aspect, per aspect step id. */
function aspectMinutes(rows: readonly TaskCycleTimeRow[]): Map<string, number> {
  const totals = new Map<string, { seconds: number; cards: number }>();
  for (const row of rows) {
    for (const [step, seconds] of Object.entries(row.reviewAspectSeconds ?? {})) {
      const total = totals.get(step) ?? { seconds: 0, cards: 0 };
      totals.set(step, { seconds: total.seconds + seconds, cards: total.cards + 1 });
    }
  }
  return new Map([...totals].map(([step, total]) => [step, total.seconds / total.cards / 60]));
}

function reviewBlock(sources: FlowHealthSources): FlowBlock {
  const week = measured(sources.windows['7d'].cycleTime?.tasks);
  const month = measured(sources.windows['30d'].cycleTime?.tasks);
  const per = (key: FlowFigureKey, read: (rows: TaskCycleTimeRow[]) => number | null) =>
    figure(key, read(week), read(month));
  const backward = (rows: TaskCycleTimeRow[]) =>
    ratio(rows.filter(row => row.backwardTransitions > 0).length, rows.length);
  const moved = week.filter(row => row.backwardTransitions > 0).length;
  const aspects7 = aspectMinutes(week);
  const aspects30 = aspectMinutes(month);
  const aspectRows = [...new Set([...aspects7.keys(), ...aspects30.keys()])].sort().map(step =>
    figure('reviewMinutesPerAspect', aspects7.get(step) ?? null, aspects30.get(step) ?? null, {
      id: `aspect:${step}`,
      label: `${FIGURE_LABELS.reviewMinutesPerAspect}: ${aspectLabel(step)}`,
    }));
  return {
    key: 'review',
    title: 'Review',
    figures: [
      per('reviewRoundsPerCard', rows => mean(rows.map(row => row.reviewRounds))),
      per('reviewMinutesPerCard', rows => {
        const value = mean(rows.map(row => row.reviewRunSeconds));
        return value == null ? null : value / 60;
      }),
      ...aspectRows,
      per('tokensPerCard', rows => mean(rows.map(row => row.cardTokens ?? 0).filter(tokens => tokens > 0))),
      per('costPerCard', rows => mean(rows.map(row => row.cardCostUsd ?? 0).filter(cost => cost > 0))),
      figure('backwardShare', backward(week), backward(month),
        week.length ? { note: `${moved} of ${week.length} delivered cards in 7 days.` } : {}),
    ],
  };
}

// ---- now ----

function slotsText(stage: FlowStageKey, slots: FlowSlots | null): string | null {
  if (slots?.busy == null || slots.total == null) return null;
  return slots.busy >= slots.total
    ? `All ${slots.total} ${SLOT_NOUNS[stage]} slots busy`
    : `${slots.busy} of ${slots.total} ${SLOT_NOUNS[stage]} slots busy`;
}

function stageWait(stage: FlowStageKey, waiting: number, oldestMinutes: number | null,
  slots: FlowSlots | null, reason: string): FlowStageWait {
  const targetMinutes = FLOW_STAGE_WAIT_TARGET_MINUTES[stage];
  const status: FlowStatus = waiting === 0 ? 'met'
    : oldestMinutes == null ? 'unknown' : oldestMinutes <= targetMinutes ? 'met' : 'missed';
  return {
    stage, label: STAGE_LABELS[stage], lane: STAGE_LANES[stage], waiting,
    oldestMinutes: waiting === 0 ? null : oldestMinutes, targetMinutes, slots,
    reason: waiting === 0 ? 'Nothing waiting' : reason, status,
  };
}

function nowBlock(sources: FlowHealthSources): FlowStageWait[] {
  const health = sources.pipelineHealth;
  const lane = (state: string) => health?.lanes.find(item => item.lane === state) ?? null;
  const age = (at: string | null | undefined) => {
    const parsed = at ? Date.parse(at) : Number.NaN;
    return Number.isFinite(parsed) ? Math.max(0, (sources.nowMs - parsed) / 60_000) : null;
  };
  const laneStage = (stage: FlowStageKey, state: string, slots: FlowSlots | null, fallback: string) => {
    const drain = lane(state);
    const stalled = drain?.isStalled ? '; nothing left the lane in the drain window' : '';
    return stageWait(stage, drain?.queueCount ?? 0, age(drain?.oldestQueuedAtUtc),
      slots, `${slotsText(stage, slots) ?? fallback}${stalled}`);
  };
  const economics = sources.windows['7d'].economics;
  const holder = economics?.gateHoldingProject;
  const gate = health?.activeGate;
  const gateReason = holder && holder !== sources.project ? `Gate held by ${holder}`
    : gate?.isHanging ? `Gate hanging for ${gate.elapsedMinutes} min`
      : slotsText('gate', sources.gateSlots) ?? 'Gate slot use is not reported';
  return [
    laneStage('pickup', TaskState.Ready, sources.codingSlots, 'Waiting for pickup'),
    laneStage('review', TaskState.AutoReview, sources.reviewSlots, 'Waiting for a review slot'),
    stageWait('gate',
      economics ? economics.queued + economics.prepared + economics.gating + economics.readyToPublish : 0,
      economics?.oldestWaitingMinutes ?? null, sources.gateSlots, gateReason),
    laneStage('decision', TaskState.HumanReview, null, 'Waiting for an operator decision'),
  ];
}

// ---- brake ----

function cards(count: number): string {
  return `${count} ${count === 1 ? 'card' : 'cards'}`;
}

function lowerFirst(text: string): string {
  return text.charAt(0).toLowerCase() + text.slice(1);
}

/** How far a missed figure is from its target; 1 is on target. */
function severity(item: FlowFigure): number {
  const value = item.value7d ?? 0;
  if (item.target.direction === 'max') return item.target.value === 0 ? Infinity : value / item.target.value;
  return value === 0 ? Infinity : item.target.value / value;
}

/**
 * The one sentence at the top. The stage whose oldest card is furthest over
 * its wait target is the brake; without such a stage, the 7-day figure
 * furthest from its target; otherwise no brake.
 */
export function deriveBrake(now: readonly FlowStageWait[], blocks: readonly FlowBlock[]): FlowBrake {
  const stalled = now.filter(item => item.status === 'missed')
    .sort((a, b) => (b.oldestMinutes ?? 0) / b.targetMinutes - (a.oldestMinutes ?? 0) / a.targetMinutes);
  const stage = stalled[0];
  if (stage) {
    const slots = stage.slots?.busy != null && stage.slots.total != null
      ? `${stage.slots.busy} of ${stage.slots.total} slots busy` : null;
    const held = stage.stage === 'gate' && stage.reason.startsWith('Gate held by') ? lowerFirst(stage.reason) : null;
    const tail = [slots, held].filter(Boolean).join(', ') || lowerFirst(stage.reason);
    return {
      stage: stage.stage, figure: null, tone: 'brake',
      sentence: `${stage.label} waits: ${cards(stage.waiting)}, oldest ${formatAge(stage.oldestMinutes)}, ${tail}`,
    };
  }
  const figures = blocks.flatMap(block => block.figures);
  const missed = figures.filter(item => item.status === 'missed').sort((a, b) => severity(b) - severity(a));
  const worst = missed[0];
  if (worst) {
    return {
      stage: null, figure: worst.key, tone: 'brake',
      sentence: `No queue over its target; ${lowerFirst(worst.label)} is ${formatFigure(worst.value7d, worst.unit)} against a target of ${formatTarget(worst.target)}`,
    };
  }
  const waiting = now.some(item => item.waiting > 0);
  // Live slot use is not flow data: idle slots alone still mean "no data yet".
  if (!waiting && figures.every(item => item.key === 'gateSlotsBusy' || item.value7d == null)) {
    return {
      stage: null, figure: null, tone: 'empty',
      sentence: 'No flow data yet: nothing waits and no card was delivered in the last 7 days',
    };
  }
  return {
    stage: null, figure: null, tone: 'calm',
    sentence: 'No brake right now: every measured stage and figure is within its target',
  };
}

function batchLifetime(sources: FlowHealthSources): string | null {
  const report = sources.batchReport;
  if (!report) return null;
  if (report.closedBatches === 0) return 'No batch has closed yet.';
  const size = report.meanBatchSize == null ? '' : `, mean ${NUMBER.format(report.meanBatchSize)} cards`;
  const green = report.batchGreenRate == null ? '' : `, ${Math.round(report.batchGreenRate * 100)}% green`;
  return `Since batching started: ${report.closedBatches} batches${size}${green}.`;
}

/** Joins the existing read models into the page's figures, waits and brake. */
export function deriveFlowHealth(sources: FlowHealthSources): FlowHealthView {
  const blocks = [groupingBlock(sources), gateBlock(sources), reviewBlock(sources)];
  const now = nowBlock(sources);
  return { brake: deriveBrake(now, blocks), blocks, now, batchLifetime: batchLifetime(sources) };
}

/** Picks this project's and the all-projects gate-economics rows per window. */
export function joinFlowSources(project: string, nowMs: number, responses: FlowHealthResponses): FlowHealthSources {
  const row = (window: FlowWindow, name: string) => responses.economics[window]?.rows
    .find(item => item.project.toLowerCase() === name.toLowerCase()) ?? null;
  const windowSources = (window: FlowWindow) => ({
    economics: row(window, project),
    economicsAll: row(window, ALL_PROJECTS_ROW),
    cycleTime: responses.cycleTime[window],
  });
  return {
    project, nowMs,
    windows: { '7d': windowSources('7d'), '30d': windowSources('30d') },
    pipelineHealth: responses.pipelineHealth,
    batchReport: responses.batchReport,
    batchingEnabled: responses.batchingEnabled,
    gateSlots: responses.gateSlots,
    reviewSlots: responses.reviewSlots,
    codingSlots: responses.codingSlots,
  };
}
