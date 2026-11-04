import type { StatusTone } from '../../../../../../components/status-indicator/status-indicator.component';

/**
 * Tone of the current steering authority receipt state (AGT-3403). Admitted
 * and requested are acknowledged facts, running is in flight, consumed is
 * done, recovered and unresolved need a look, quarantined and rejected are
 * blocked; a later backend state falls back to neutral.
 */
const STEERING_RECEIPT_TONES: Readonly<Record<string, StatusTone>> = {
  admitted: 'info',
  requested: 'info',
  running: 'running',
  consumed: 'success',
  recovered: 'warning',
  unresolved: 'warning',
  stopped: 'neutral',
  quarantined: 'danger',
  rejected: 'danger',
};

export function steeringReceiptTone(state: string | null | undefined): StatusTone {
  const key = (state ?? '').trim().toLowerCase();
  return Object.hasOwn(STEERING_RECEIPT_TONES, key) ? STEERING_RECEIPT_TONES[key] : 'neutral';
}
