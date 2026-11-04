import type { StatusTone } from '../../../components/status-indicator/status-indicator.component';
import type { HealthState, StepState } from './deploy-area.model';

/**
 * Deploy run and step states mapped onto the shared status tones (AGT-3403).
 * Waiting states are a neutral fact worth noticing (info); a state the backend
 * adds later falls back to neutral.
 */
const RUN_STATE_TONE: Readonly<Record<StepState, StatusTone>> = {
  succeeded: 'success',
  failed: 'danger',
  running: 'running',
  queued: 'info',
  'awaiting-approval': 'info',
  'waiting-for-intervention': 'info',
  cancelled: 'neutral',
  unknown: 'neutral',
  pending: 'neutral',
  skipped: 'neutral',
};

export function runStateTone(state: string | null | undefined): StatusTone {
  return state && Object.hasOwn(RUN_STATE_TONE, state) ? RUN_STATE_TONE[state as StepState] : 'neutral';
}

/** Environment health as the Deploy overview card shows it. */
const HEALTH_TONE: Readonly<Record<HealthState, StatusTone>> = {
  healthy: 'success',
  up: 'success',
  degraded: 'warning',
  unhealthy: 'danger',
  down: 'danger',
  invalid: 'danger',
  unknown: 'neutral',
};

export function healthStateTone(state: string | null | undefined): StatusTone {
  return state && Object.hasOwn(HEALTH_TONE, state) ? HEALTH_TONE[state as HealthState] : 'neutral';
}

/**
 * A run in a list of finished runs (history, timeline markers, runbook runs):
 * a past failure stays calm (R4), so it reads as warning, not danger.
 */
export function historyRunStateTone(state: string | null | undefined): StatusTone {
  const tone = runStateTone(state);
  return tone === 'danger' ? 'warning' : tone;
}
