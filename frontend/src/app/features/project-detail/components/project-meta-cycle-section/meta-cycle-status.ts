import type { StatusTone } from '../../../../components/status-indicator/status-indicator.component';
import type { MetaCycleVerdict } from '../../../../models/supervisor.model';

/**
 * Meta-cycle verdict to status tone (AGT-3403). `null` is "no cycle yet"
 * (idle). Unknown verdicts fall back to `neutral`.
 */
const META_CYCLE_VERDICT_TONES: Readonly<Record<MetaCycleVerdict, StatusTone>> = {
  healthy: 'success',
  fixTriggering: 'warning',
  escalationOnly: 'danger',
  aborted: 'neutral',
};

/** Current verdict (status pill, last cycle). */
export function metaCycleVerdictTone(verdict: MetaCycleVerdict | null | undefined): StatusTone {
  return (verdict && META_CYCLE_VERDICT_TONES[verdict]) || 'neutral';
}

/**
 * Verdict of a past cycle in the history list. History never keeps an acute
 * tone (hard rule R4), so an escalation in the past reads as warning.
 */
export function metaCycleHistoryTone(verdict: MetaCycleVerdict | null | undefined): StatusTone {
  const tone = metaCycleVerdictTone(verdict);
  return tone === 'danger' ? 'warning' : tone;
}
