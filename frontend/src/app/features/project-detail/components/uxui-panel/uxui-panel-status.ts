import type { StatusTone } from '../../../../components/status-indicator/status-indicator.component';
import { verdictTone } from '../security-panel/security-panel-status';

/**
 * Design status is free text from the design loop or brief. It reuses the
 * security verdict keywords (ok/pass, stale/warn, fail/crit); anything else,
 * including "no iteration", stays neutral (AGT-3403).
 */
export function designStatusTone(status: string | null | undefined): StatusTone {
  return verdictTone(status);
}
