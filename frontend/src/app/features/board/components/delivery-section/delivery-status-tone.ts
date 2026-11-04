import type { StatusTone } from '../../../../components/status-indicator/status-indicator.component';
import type { DeliveryTone } from '../../../../models/delivery-presentation';

/**
 * Delivery tone to shared status tone (AGT-3403). `active` covers queued and
 * running deploy runs; a tone added later falls back to neutral.
 */
const DELIVERY_STATUS_TONES: Readonly<Record<DeliveryTone, StatusTone>> = {
  ok: 'success',
  active: 'running',
  attention: 'warning',
  danger: 'danger',
  neutral: 'neutral',
};

export function deliveryStatusTone(tone: DeliveryTone | string | null | undefined): StatusTone {
  return DELIVERY_STATUS_TONES[tone as DeliveryTone] ?? 'neutral';
}
