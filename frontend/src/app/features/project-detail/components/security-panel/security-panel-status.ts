import type { StatusTone } from '../../../../components/status-indicator/status-indicator.component';
import type { SecurityBaselineBadge } from './security-panel.types';

/** Tone of the baseline badge and baseline status text (AGT-3403). */
const BASELINE_TONES: Readonly<Record<SecurityBaselineBadge, StatusTone>> = {
  ok: 'success',
  stale: 'warning',
  failing: 'danger',
  missing: 'neutral',
  unknown: 'neutral',
};

export function baselineTone(badge: string): StatusTone {
  return BASELINE_TONES[badge as SecurityBaselineBadge] ?? 'neutral';
}

/** Tone of a free-text review verdict: ok/pass, stale/warn, fail/crit; anything else is neutral. */
export function verdictTone(verdict: string | null | undefined): StatusTone {
  if (!verdict) return 'neutral';
  const v = verdict.toLowerCase();
  if (v.includes('ok') || v.includes('pass')) return 'success';
  if (v.includes('stale') || v.includes('warn')) return 'warning';
  if (v.includes('fail') || v.includes('crit')) return 'danger';
  return 'neutral';
}

/**
 * Tone of a verdict in the review history list. A past failure is history, not
 * an acute state (hard rule R4), so it reads as warning; the current verdict
 * card keeps `verdictTone`.
 */
export function historyVerdictTone(verdict: string | null | undefined): StatusTone {
  const tone = verdictTone(verdict);
  return tone === 'danger' ? 'warning' : tone;
}
