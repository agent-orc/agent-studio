import type { StatusTone } from '@studio/ui/components/status-indicator/status-indicator.component';
import { HEALTH_LABEL, type DeployHealth, type HealthState, type StepState } from './deploy-area.model';

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

/**
 * Environment health as the Deploy overview card shows it. Misconfigured is a
 * declaration problem the owner fixes, not an outage, so it reads as warning.
 */
const HEALTH_TONE: Readonly<Record<HealthState, StatusTone>> = {
  healthy: 'success',
  up: 'success',
  degraded: 'warning',
  unhealthy: 'danger',
  down: 'danger',
  invalid: 'warning',
  unknown: 'neutral',
  'crash-loop': 'danger',
  'crash-rollback': 'danger',
  'not-observed': 'neutral',
  'not-deployed': 'neutral',
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

/**
 * Environment health of {@link buildEnvironmentCards} on the shared status
 * indicator. Every surface that names an environment state renders this.
 */
export function environmentHealthDisplay(health: DeployHealth): { tone: StatusTone; label: string } {
  if (health.status === 'no-checks') return { tone: 'neutral', label: 'No checks' };
  return { tone: healthStateTone(health.state), label: HEALTH_LABEL[health.state] };
}
