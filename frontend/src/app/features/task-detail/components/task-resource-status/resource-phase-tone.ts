import type { StatusTone } from '../../../../components/status-indicator/status-indicator.component';
import type { ResourcePhase } from '../../state/task-resource-states';

/**
 * Tone of an independent task resource phase (AGT-3403). Warming and loading
 * are work in progress; stale and unavailable degrade one pane while the rest
 * of the task stays usable; only a request error is acute.
 */
const RESOURCE_PHASE_TONES: Readonly<Record<ResourcePhase, StatusTone>> = {
  idle: 'neutral',
  loading: 'running',
  warming: 'running',
  ready: 'success',
  stale: 'warning',
  unavailable: 'warning',
  error: 'danger',
};

/** Unknown phases fall back to neutral. */
export function resourcePhaseTone(phase: string | null | undefined): StatusTone {
  return RESOURCE_PHASE_TONES[phase as ResourcePhase] ?? 'neutral';
}
