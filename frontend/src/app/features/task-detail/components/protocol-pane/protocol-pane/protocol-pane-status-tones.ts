import type { StatusTone } from '../../../../../components/status-indicator/status-indicator.component';
import type { WatchdogPillState } from '../watchdog-state';

/** Claude session continuity after the last CLI invocation. */
export type SessionChipKind = 'continued' | 'lost' | 'fresh';

/** Interim status request lifecycle in the protocol pane. */
export type InterimSummaryStatus = 'idle' | 'pending' | 'ready' | 'failed';

const SESSION_CHIP_TONES: Readonly<Record<SessionChipKind, StatusTone>> = {
  continued: 'success',
  fresh: 'info',
  lost: 'warning',
};

const WATCHDOG_TONES: Readonly<Record<WatchdogPillState, StatusTone>> = {
  healthy: 'running',
  quiet: 'warning',
  suspicious: 'warning',
  hung: 'danger',
  idle: 'neutral',
};

const INTERIM_TONES: Readonly<Record<InterimSummaryStatus, StatusTone>> = {
  idle: 'neutral',
  pending: 'running',
  ready: 'info',
  failed: 'danger',
};

/** Status tones of the protocol pane header chips and banners (AGT-3403). */
export function sessionChipTone(kind: string): StatusTone {
  return SESSION_CHIP_TONES[kind as SessionChipKind] ?? 'neutral';
}

export function watchdogTone(state: string): StatusTone {
  return WATCHDOG_TONES[state as WatchdogPillState] ?? 'neutral';
}

/** `allowed` is fine, any other reported window status needs attention, none is unknown. */
export function rateLimitTone(status: string | null | undefined): StatusTone {
  if (!status) return 'neutral';
  return status === 'allowed' ? 'success' : 'warning';
}

export function interimTone(status: string): StatusTone {
  return INTERIM_TONES[status as InterimSummaryStatus] ?? 'neutral';
}
