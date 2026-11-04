import type { StatusTone } from '../../../../components/status-indicator/status-indicator.component';
import type { ProductRuntimeLevel } from '../../../../models/product-runtime.model';

/** Severity order of the log levels, used by the minimum-level filter. */
export const LEVEL_RANK: Readonly<Record<ProductRuntimeLevel, number>> = {
  Trace: 0, Debug: 1, Info: 2, Warn: 3, Error: 4, Fatal: 5,
};

/** Event log level on the shared indicator (AGT-3403). */
const LEVEL_TONES: Readonly<Record<ProductRuntimeLevel, StatusTone>> = {
  Trace: 'neutral',
  Debug: 'neutral',
  Info: 'neutral',
  Warn: 'warning',
  Error: 'danger',
  Fatal: 'danger',
};

export function runtimeLevelTone(level: string | null | undefined): StatusTone {
  return level && Object.hasOwn(LEVEL_TONES, level) ? LEVEL_TONES[level as ProductRuntimeLevel] : 'neutral';
}

/** Event outcome on the shared indicator; unknown outcomes stay neutral. */
const STATUS_TONES: Readonly<Record<string, StatusTone>> = {
  Ok: 'success',
  Failed: 'danger',
  Timeout: 'danger',
  Cancelled: 'neutral',
  Skipped: 'neutral',
};

export function runtimeStatusTone(status: string | null | undefined): StatusTone {
  return status && Object.hasOwn(STATUS_TONES, status) ? STATUS_TONES[status] : 'neutral';
}
