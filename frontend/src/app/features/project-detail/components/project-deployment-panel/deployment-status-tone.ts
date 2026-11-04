import type { StatusTone } from '../../../../components/status-indicator/status-indicator.component';

/**
 * Status tones of the repository deployment views (AGT-3403). Raw wire values
 * that this build does not know fall back to neutral.
 */

/** `environments[].health` from the latest host report. */
const ENVIRONMENT_HEALTH_TONE: Readonly<Record<string, StatusTone>> = { healthy: 'success', failed: 'danger', unknown: 'neutral' };

/**
 * Host report statuses and deploy-stable run statuses in a history list: a
 * past failure stays calm (R4) and reads as warning.
 */
const HISTORY_STATUS_TONE: Readonly<Record<string, StatusTone>> = {
  healthy: 'success', ok: 'success', succeeded: 'success', 'rolled-back': 'warning', failed: 'warning', unknown: 'neutral',
};

function lookup(table: Readonly<Record<string, StatusTone>>, value: string | null | undefined): StatusTone {
  return value && Object.hasOwn(table, value) ? table[value] : 'neutral';
}

export function environmentHealthTone(health: string | null | undefined): StatusTone {
  return lookup(ENVIRONMENT_HEALTH_TONE, health);
}

export function deploymentHistoryTone(status: string | null | undefined): StatusTone {
  return lookup(HISTORY_STATUS_TONE, status);
}

/** The latest deploy-stable run is current truth: anything but ok is a failure right now. */
export function lastDeploymentTone(status: string | null | undefined): StatusTone {
  if (!status || status === 'unknown') return 'neutral';
  return status === 'ok' ? 'success' : 'danger';
}

export type TargetReadiness = 'ready' | 'setup-required' | 'idle-confirmed' | 'idle-required';

export const TARGET_READINESS: Readonly<Record<TargetReadiness, { tone: StatusTone; label: string }>> = {
  ready: { tone: 'success', label: 'Ready' },
  'setup-required': { tone: 'warning', label: 'Setup required' },
  'idle-confirmed': { tone: 'success', label: 'Idle confirmed' },
  'idle-required': { tone: 'warning', label: 'Idle required' },
};
