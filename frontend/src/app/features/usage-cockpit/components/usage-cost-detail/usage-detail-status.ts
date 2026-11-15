import type { StatusTone } from '../../../../components/status-indicator/status-indicator.component';
import type { UsageChipState } from '../../usage-chip.util';

/**
 * Status tone of a usage data state in the usage detail (AGT-3403): complete
 * data is success, loading is running, stale, unverified and partial data need
 * attention, an unavailable or unknown state stays neutral.
 */
const USAGE_STATE_TONES: Readonly<Record<UsageChipState, StatusTone>> = {
  normal: 'success',
  loading: 'running',
  stale: 'warning',
  suspicious: 'warning',
  partial: 'warning',
  unknown: 'neutral',
};

export function usageStateTone(state: string | null | undefined): StatusTone {
  return USAGE_STATE_TONES[state as UsageChipState] ?? 'neutral';
}

/** Tone of a live run's inclusion in today's total. */
export function liveRunInclusionTone(included: boolean): StatusTone {
  return included ? 'success' : 'neutral';
}
