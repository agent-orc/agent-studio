import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { throwError } from 'rxjs';
import { ConnectionStatusService } from './connection-status.service';
import { NotificationService } from './notification.service';

/**
 * Refuses *mutating* backend calls while the app is offline, so an action the
 * operator takes during a backend outage fails loudly instead of silently
 * (AC3 of the backend-offline-warning feature). Reads pass through untouched:
 * already-loaded data stays usable and reconnect re-hydration never gets in
 * the way of recovery. Reference-status batches and TokenEconomy calculations
 * use POST for their request bodies but remain side-effect-free reads.
 *
 * "Offline" is the debounced {@link ConnectionStatusService.offline} signal
 * (the SignalR socket has been down past the grace window), the same trigger
 * the top banner reads — so the block and the banner are always in lockstep.
 *
 * The synthesized error mirrors a real "backend unreachable" failure
 * (`status: 0`) so existing per-call error handlers and the error dialog render
 * their usual "backend not reachable" copy. A throttled warning toast fires on
 * top so even call sites that swallow their error never fail silently.
 */
const MUTATING_METHODS = new Set(['POST', 'PUT', 'DELETE', 'PATCH']);
const READ_ONLY_POST_URLS = new Set([
  '/api/tasks/reference-status',
  '/api/token-pricing/calculate',
]);

// One toast per burst: a single user action can fan out several writes, and a
// stack of identical "backend offline" toasts is noise, not signal.
const TOAST_THROTTLE_MS = 3000;
let lastToastAt = 0;

/** Test-only reset for the module-level toast throttle. */
export function __resetOfflineGuardThrottle(): void {
  lastToastAt = 0;
}

export const offlineGuardInterceptor: HttpInterceptorFn = (req, next) => {
  const conn = inject(ConnectionStatusService);
  const notify = inject(NotificationService);

  const isOwnApi = req.url.startsWith('/api');
  const method = req.method.toUpperCase();
  const isReadOnlyPost = method === 'POST' && READ_ONLY_POST_URLS.has(req.url);
  const isMutating = MUTATING_METHODS.has(method) && !isReadOnlyPost;

  if (conn.offline() && isOwnApi && isMutating) {
    const now = Date.now();
    if (now - lastToastAt >= TOAST_THROTTLE_MS) {
      lastToastAt = now;
      notify.warning(
        'The backend is offline, so this action was not sent. It will work again automatically once the connection is back.',
        'Action blocked — backend offline',
      );
    }

    const error = new HttpErrorResponse({
      status: 0,
      statusText: 'Backend offline',
      url: req.url,
      error: { error: 'Backend offline — action blocked until the connection returns.' },
    });
    return throwError(() => error);
  }

  return next(req);
};
