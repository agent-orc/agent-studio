import { HttpInterceptorFn, HttpResponse } from '@angular/common/http';
import { inject } from '@angular/core';
import { tap } from 'rxjs';
import { GitStateStampService } from './git-state-stamp.service';

export const GIT_STATE_AT_HEADER = 'X-Git-State-At';
export const GIT_STATE_STALE_HEADER = 'X-Git-State-Stale';

/**
 * Reads the git-state freshness stamp off any response that carries it.
 *
 * Doing this once in the HTTP layer keeps the board, the task list and the
 * Project Hub inventory call sites untouched: the backend adds the two headers
 * to whichever responses read indexed git state, and the stamp is available to
 * any component through {@link GitStateStampService}.
 */
export const gitStateStampInterceptor: HttpInterceptorFn = (req, next) => {
  const stamps = inject(GitStateStampService);
  return next(req).pipe(
    tap(event => {
      if (!(event instanceof HttpResponse)) return;
      stamps.record(
        event.headers.get(GIT_STATE_AT_HEADER),
        event.headers.get(GIT_STATE_STALE_HEADER),
      );
    }),
  );
};
