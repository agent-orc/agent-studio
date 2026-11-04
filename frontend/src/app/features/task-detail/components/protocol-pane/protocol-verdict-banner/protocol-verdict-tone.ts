import type { StatusTone } from '../../../../../components/status-indicator/status-indicator.component';
import type { AuthoritativeRunOutcomeStatus, ProtocolVerdict } from '../protocol-verdict';

const VERDICT_TONES: Readonly<Record<AuthoritativeRunOutcomeStatus, StatusTone>> = {
  succeeded: 'success',
  failed: 'danger',
  'needs-decision': 'warning',
  unclear: 'neutral',
};

const SIGNAL_TONES: Readonly<Record<AuthoritativeRunOutcomeStatus, StatusTone>> = {
  succeeded: 'success',
  failed: 'danger',
  'needs-decision': 'warning',
  unclear: 'warning',
};

/**
 * Tone of the run outcome verdict (AGT-3403). An unclear verdict while the
 * run is still active reads as running; an unknown status stays neutral.
 */
export function protocolVerdictTone(verdict: Pick<ProtocolVerdict, 'status' | 'label'>, running = false): StatusTone {
  if (verdict.status === 'unclear' && (running || verdict.label === 'Running')) return 'running';
  return VERDICT_TONES[verdict.status] ?? 'neutral';
}

/** Tone of one raw outcome signal; an unclear signal is worth a look. */
export function protocolSignalTone(status: AuthoritativeRunOutcomeStatus | string): StatusTone {
  return SIGNAL_TONES[status as AuthoritativeRunOutcomeStatus] ?? 'neutral';
}
