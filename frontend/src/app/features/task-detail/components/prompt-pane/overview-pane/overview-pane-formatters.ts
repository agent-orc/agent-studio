import { laneName } from '../../../../../models/lane-presentation';
import type { StudioIconName } from '../../../../../components/studio-icon/studio-icon.component';
import type { PipelineStepStatus, StepKind } from '../../../../task-pipeline';

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

export function formatTokens(value: number): string {
  if (value <= 0) return '—';
  if (value < 1000) return String(value);
  const scale = value < 1_000_000 ? 1000 : 1_000_000;
  const suffix = value < 1_000_000 ? 'k' : 'm';
  return `${(value / scale).toFixed(1).replace(/\.0$/, '')}${suffix}`;
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
