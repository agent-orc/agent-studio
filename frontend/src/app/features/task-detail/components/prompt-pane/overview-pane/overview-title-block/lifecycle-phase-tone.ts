import type { StatusTone } from '../../../../../../components/status-indicator/status-indicator.component';

/**
 * Tone of the lifecycle phase chip in the task-detail title (AGT-3403). Work
 * in flight is running, blocked phases are danger, stalls and operator waits
 * are warning, intentional handoffs are info. A phase the backend adds later
 * stays neutral.
 */
const LIFECYCLE_PHASE_TONES: Readonly<Record<string, StatusTone>> = {
  'human-ready': 'neutral',
  'intake-running': 'running',
  'intake-blocked': 'danger',
  'intake-passed': 'success',
  'execution-running': 'running',
  'execution-stalled': 'warning',
  'quota-waiting': 'warning',
  'loop-waiting': 'info',
  'steer-pending': 'warning',
  'post-processing-running': 'running',
  'post-processing-blocked': 'danger',
  'awaiting-review': 'info',
  'integrating': 'running',
};

export function lifecyclePhaseTone(phase: string | null | undefined): StatusTone {
  if (!phase || !Object.hasOwn(LIFECYCLE_PHASE_TONES, phase)) return 'neutral';
  return LIFECYCLE_PHASE_TONES[phase];
}
