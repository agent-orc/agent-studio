import { laneName } from '../../../../models/lane-presentation';
import type {
  CycleTimeAggregate,
  CycleTimeStageKey,
  CycleTimeWindow,
} from '../../models/project-cycle-time.model';

/**
 * Compact duration for dense tables: `42s`, `3.5m`, `2.1h`, `1.4d`. One unit,
 * one decimal at most, tabular-friendly. Null renders as an en dash placeholder
 * the template decides on, so the helper returns null for "no value".
 */
export function formatDuration(seconds: number | null | undefined): string | null {
  if (seconds === null || seconds === undefined || !Number.isFinite(seconds)) return null;
  const s = Math.max(0, seconds);
  // Pick the unit after rounding: 59.6 s reads "1m", not "60s"; 23.97 h reads "1d", not "24h".
  if (Math.round(s) < 60) return `${Math.round(s)}s`;
  if (round1(s / 60) < 60) return `${trim(s / 60)}m`;
  if (round1(s / 3600) < 24) return `${trim(s / 3600)}h`;
  return `${trim(s / 86_400)}d`;
}

function round1(value: number): number {
  return Math.round(value * 10) / 10;
}

function trim(value: number): string {
  const rounded = round1(value);
  return Number.isInteger(rounded) ? String(rounded) : rounded.toFixed(1);
}

export function formatCount(value: number | null | undefined): string | null {
  if (value === null || value === undefined || !Number.isFinite(value)) return null;
  return Number.isInteger(value) ? String(value) : value.toFixed(1);
}

/** Window label for the selector and the summary line. */
export function windowLabel(window: CycleTimeWindow): string {
  switch (window) {
    case '7d': return 'Last 7 days';
    case '30d': return 'Last 30 days';
    case 'all': return 'All time';
  }
}

/** Short ISO-like timestamp (`2026-08-21 06:44`) in the viewer's local time. */
export function formatTimestamp(iso: string | null | undefined): string {
  if (!iso) return '';
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return iso;
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

/**
 * Lane labels for the matrix and the transition tables. AGT-2715: this switch
 * used to carry its own vocabulary ("Progress", "Human Review", "Completed")
 * that disagreed with the board and the detail view; it now reads the lane
 * presentation catalogue. An absent lane still renders as "unknown" because a
 * cycle-time cell can legitimately have no source lane.
 */
export function laneLabel(lane: string | null | undefined): string {
  if (!lane) return 'unknown';
  return laneName(lane);
}

/**
 * Matrix shading level 0..4 for a cell count against the largest off-diagonal
 * count: 0 for empty, then four equal-width bands on a square-root scale so a
 * few very large cells do not flatten every other cell to the same tint.
 */
export function matrixLevel(count: number, max: number): number {
  if (count <= 0 || max <= 0) return 0;
  const ratio = Math.sqrt(count) / Math.sqrt(max);
  return Math.min(4, Math.max(1, Math.ceil(ratio * 4)));
}

export interface CompositionSegment {
  stage: CycleTimeStageKey;
  label: string;
  /** Median seconds of the stage over the tasks where it occurred. */
  seconds: number;
  /** Share of the bar, 0..100, over the sum of the segment medians. */
  percent: number;
  highlighted: boolean;
  count: number;
  p90: number | null;
}

/**
 * Stacked-bar composition from the stage aggregates: one segment per additive
 * stage with a median, in lane order. Stages that never occurred are omitted so
 * the bar does not render empty slivers. The widths use the sum of the segment
 * medians as the reference; medians are not additive, so the bar reads as a
 * relative profile, not as the lead-time median.
 */
export function compositionSegments(aggregates: readonly CycleTimeAggregate[]): CompositionSegment[] {
  const stages = aggregates.filter(a => a.kind === 'stage' && a.p50 !== null && a.p50 > 0 && a.count > 0);
  const total = stages.reduce((sum, a) => sum + (a.p50 ?? 0), 0);
  if (total <= 0) return [];
  return stages.map(a => ({
    stage: a.stage as CycleTimeStageKey,
    label: a.label,
    seconds: a.p50 ?? 0,
    percent: ((a.p50 ?? 0) / total) * 100,
    highlighted: a.highlighted,
    count: a.count,
    p90: a.p90,
  }));
}
