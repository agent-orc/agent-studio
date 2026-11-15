import type { StatusTone } from '../../../../components/status-indicator/status-indicator.component';
import type { DriftScoreBand } from '../../../../models/drift.model';

/** Status-indicator tone of a drift report score band (AGT-3403); unknown bands stay neutral. */
const DRIFT_SCORE_BAND_TONES: Readonly<Record<DriftScoreBand, StatusTone>> = {
  Healthy: 'success',
  Watch: 'info',
  Warn: 'warning',
  Critical: 'danger',
  Unknown: 'neutral',
};

export function driftScoreBandTone(band: DriftScoreBand | string | null | undefined): StatusTone {
  return band && Object.hasOwn(DRIFT_SCORE_BAND_TONES, band)
    ? DRIFT_SCORE_BAND_TONES[band as DriftScoreBand]
    : 'neutral';
}
