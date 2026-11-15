import type { StatusTone } from '../../../../../components/status-indicator/status-indicator.component';

const ENRICHMENT_STATUS_TONES: Readonly<Record<string, StatusTone>> = {
  enriched: 'success',
  'fallback-unenriched': 'warning',
  blocked: 'danger',
  unchanged: 'neutral',
};

/** Tone of the prompt enrichment status (AGT-3403); unknown statuses stay neutral. */
export function enrichmentStatusTone(status: string): StatusTone {
  return Object.hasOwn(ENRICHMENT_STATUS_TONES, status) ? ENRICHMENT_STATUS_TONES[status] : 'neutral';
}
