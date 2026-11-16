/**
 * AGT-3840: backend `HeldCapabilityCauses` read as a short phrase, shared by
 * the Execution Hosts rows and the card line of a review held on a missing key.
 */
const HELD_CAUSE_LABELS: Record<string, string> = {
  'not-registered': 'not registered',
  stale: 'advertisement expired',
  'not-ready': 'not ready',
  'probe-indeterminate': 'probe indeterminate',
  'credential-invalid': 'credential invalid',
  'provider-incident': 'provider incident',
  'route-excluded': 'provider routed away',
  'no-read-proof': 'no read proof',
};

/** Short reading of a hold cause for a row or a card sentence. */
export function heldCauseLabel(cause: string | null | undefined): string {
  return HELD_CAUSE_LABELS[cause ?? ''] ?? 'not ready';
}
