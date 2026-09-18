import { laneName } from '../../../../../models/lane-presentation';
import type { StudioIconName } from '../../../../../components/studio-icon/studio-icon.component';
import type { PipelineStepStatus, StepKind } from '../../../../task-pipeline';
import type { PipelineRowVm } from './pipeline-row.vm';

export { stepKindLabel } from '../../../../task-pipeline';

type PipelineDisplayStatus = PipelineStepStatus | 'disabled' | 'not-run';

export function stepKindIcon(kind: StepKind): StudioIconName {
  switch (kind) {
    case 'module':       return 'sliders';
    case 'core':         return 'bot';
    case 'aspect':       return 'eye';
    case 'orchestrator': return 'branch';
    case 'tool':         return 'cli';
    case 'analysis':     return 'search';
    case 'drift':        return 'diff';
    default:             return 'dot';
  }
}

export function stepStatusIcon(status: PipelineDisplayStatus): string {
  switch (status) {
    case 'passed':   return '✅';
    case 'failed':   return '❌';
    case 'running':  return '▶️';
    case 'skipped':  return '⏭️';
    case 'notApplicable': return '−';
    case 'not-run':  return '○';
    case 'planned':  return '🕓';
    case 'disabled': return '🚫';
    default:         return '·';
  }
}

export function historicalStepStatusIcon(status: PipelineDisplayStatus): string {
  switch (status) {
    case 'passed':   return '✓';
    case 'failed':   return '×';
    case 'running':  return '›';
    case 'skipped':  return '↷';
    case 'notApplicable': return '−';
    case 'not-run':  return '○';
    case 'planned':  return '○';
    case 'disabled': return '−';
    default:         return '·';
  }
}

export function stepStatusLabel(status: PipelineDisplayStatus): string {
  switch (status) {
    case 'passed':   return 'Passed';
    case 'failed':   return 'Failed';
    case 'running':  return 'Running';
    case 'skipped':  return 'Skipped';
    case 'notApplicable': return 'Not applicable';
    case 'not-run':  return 'Not run';
    case 'planned':  return 'Planned';
    case 'disabled': return 'Disabled';
    default:         return 'Pending';
  }
}

/**
 * Lane display name for the Overview tab's lane pill. Delegates to the lane
 * presentation catalogue (AGT-2715); this switch used to be one of the five
 * competing label sources.
 */
export const laneLabel = laneName;

/** Compact token count used consistently throughout the Overview tab. */
export function formatTokens(value: number): string {
  if (!value) return '0';
  if (value < 1000) return String(value);
  if (value < 1_000_000) return `${Math.round(value / 1000)}k`;
  const millions = (value / 1_000_000).toFixed(2).replace(/0$/, '');
  return `${millions}M`;
}

export function formatDuration(seconds: number): string {
  if (seconds < 60) return `${Math.round(seconds)}s`;
  const min = Math.floor(seconds / 60);
  const sec = Math.round(seconds % 60);
  if (min < 60) return sec > 0 ? `${min}m ${sec}s` : `${min}m`;
  const hrs = Math.floor(min / 60);
  const remMin = min % 60;
  return remMin > 0 ? `${hrs}h ${remMin}m` : `${hrs}h`;
}

export function runStatusIcon(status: string): string {
  switch (status) {
    case 'completed': return '✅';
    case 'failed':    return '❌';
    case 'cancelled': return '⚠️';
    case 'running':   return '▶️';
    default:          return '❓';
  }
}

/**
 * Per-step duration for the pipeline rows. Sub-second steps (most deterministic
 * Tool steps) show in ms; longer steps fall through to the coarser m/s/h
 * formatter. Returns an em-dash when nothing ran yet.
 */
export function formatStepDuration(ms: number): string {
  if (ms <= 0) return '\u2014';
  if (ms < 1000) return `${Math.round(ms)}ms`;
  return formatDuration(ms / 1000);
}

/**
 * Effective duration in ms for a step row: a live "now - startedAt" while the
 * step is running (so the cell ticks up), otherwise the recorded `durationMs`.
 * `nowMs` is passed in rather than read from the clock so every caller ticks on
 * one signal and the function stays pure.
 */
export function liveStepDurationMs(row: PipelineRowVm, nowMs: number): number {
  if (row.status === 'running' && row.startedAt) {
    const start = new Date(row.startedAt).getTime();
    if (!Number.isNaN(start)) return Math.max(0, nowMs - start);
  }
  return row.durationMs;
}

/** Wall-clock "HH:MM" for a step timestamp; empty string when unset. */
export function formatClock(iso: string | null): string {
  if (!iso) return '';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  return d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
}

export function formatAbsoluteTime(iso: string): string {
  if (!iso) return '';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return iso;
  return d.toLocaleString();
}

export function formatRelativeTime(iso: string): string {
  if (!iso) return '';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return iso;
  const diffMs = Date.now() - d.getTime();
  const minutes = Math.round(diffMs / 60_000);
  if (minutes < 1) return 'just now';
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.round(minutes / 60);
  if (hours < 24) return `${hours}h ago`;
  const days = Math.round(hours / 24);
  if (days < 30) return `${days}d ago`;
  const months = Math.round(days / 30);
  if (months < 12) return `${months}mo ago`;
  return `${Math.round(months / 12)}y ago`;
}
