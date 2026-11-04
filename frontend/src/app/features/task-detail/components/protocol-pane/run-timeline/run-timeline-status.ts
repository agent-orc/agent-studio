import type { StatusTone } from '../../../../../components/status-indicator/status-indicator.component';

const RUN_STATUS_TONES: Readonly<Record<string, StatusTone>> = {
  running: 'running',
  completed: 'success',
  failed: 'danger',
  stopped: 'warning',
  cancelled: 'warning',
};

/**
 * Tone of a run card status (AGT-3403). Only the latest run is current truth;
 * an older failure is history and reads as warning (R4). Unknown is neutral.
 */
export function runCardStatusTone(status: string, latest: boolean): StatusTone {
  const tone = Object.hasOwn(RUN_STATUS_TONES, status) ? RUN_STATUS_TONES[status] : 'neutral';
  return tone === 'danger' && !latest ? 'warning' : tone;
}

/** Visible run card status word. */
export function runCardStatusLabel(status: string): string {
  if (status === 'running') return 'running';
  if (status === 'completed') return 'completed';
  if (status === 'failed') return 'failed';
  // 'stopped' is the deliberate-kill status (user pause, Pause-&-Send,
  // watchdog). Legacy run records may still carry 'cancelled'.
  if (status === 'stopped' || status === 'cancelled') return 'stopped';
  return status || 'unknown';
}
