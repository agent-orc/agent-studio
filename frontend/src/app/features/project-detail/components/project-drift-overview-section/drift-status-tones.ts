import type { StatusTone } from '../../../../components/status-indicator/status-indicator.component';
import type { DriftFindingStatus, DriftReport, DriftScoreBand, DriftSeverity } from '../../../../models/drift.model';

/*
 * State-to-tone mapping for the project Drift surfaces (Drift overview and
 * the architecture drift section). Keys are lowercase because the wire sends
 * either casing; an unknown value falls back to neutral.
 */

const SEVERITY_TONES: Record<Lowercase<DriftSeverity>, StatusTone> = {
  info: 'neutral',
  warn: 'warning',
  high: 'warning',
  critical: 'danger',
};

const FINDING_STATUS_TONES: Record<Lowercase<DriftFindingStatus>, StatusTone> = {
  new: 'neutral',
  tracked: 'info',
  accepted: 'success',
  ignored: 'neutral',
  resolved: 'success',
};

const SCORE_BAND_TONES: Record<Lowercase<DriftScoreBand>, StatusTone> = {
  healthy: 'success',
  watch: 'info',
  warn: 'warning',
  critical: 'danger',
  unknown: 'neutral',
};

function lookup<K extends string>(table: Record<K, StatusTone>, value: string | null | undefined): StatusTone {
  const key = (value ?? '').toString().toLowerCase() as K;
  return table[key] ?? 'neutral';
}

/** Drift severity Info / Warn / High / Critical. */
export function driftSeverityTone(severity: string | null | undefined): StatusTone {
  return lookup(SEVERITY_TONES, severity);
}

/** Drift tracking status New / Tracked / Accepted / Ignored / Resolved. */
export function driftFindingStatusTone(status: string | null | undefined): StatusTone {
  return lookup(FINDING_STATUS_TONES, status);
}

/** Drift report score band Healthy / Watch / Warn / Critical / Unknown. */
export function driftScoreBandTone(band: string | null | undefined): StatusTone {
  return lookup(SCORE_BAND_TONES, band);
}

/** Drift enum label with a leading capital, regardless of wire case ('warn' -> 'Warn'). */
export function driftEnumLabel(value: string | null | undefined): string {
  if (!value) return '';
  const str = value.toString();
  return str.charAt(0).toUpperCase() + str.slice(1).toLowerCase();
}

/** History-row notice for a report without a valid JSON sidecar. parseStatus compares case-insensitively. */
export function driftParseNotice(r: Pick<DriftReport, 'parseStatus' | 'parseError'>): { label: string; detail: string } {
  return (r.parseStatus ?? '').toString().toLowerCase() === 'malformedjson'
    ? { label: 'malformed', detail: r.parseError ?? 'sidecar invalid' }
    : { label: 'unstructured', detail: 'no JSON sidecar' };
}
