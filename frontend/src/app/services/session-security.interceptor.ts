import { inject } from '@angular/core';
import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { catchError, throwError } from 'rxjs';
import { AuthSessionState } from './auth.service';

const SAFE_METHODS = new Set(['GET', 'HEAD', 'OPTIONS']);

/** Adds the per-session CSRF proof to same-origin mutations. Passwords and session credentials never enter browser storage. */
export const sessionSecurityInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(AuthSessionState);
  if (!req.url.startsWith('/api/')) return next(req);
  // Ueber HTTPS heisst der Cookie __Host-agentstudio-csrf, ueber HTTP (lokaler
  // Dev-Betrieb) agentstudio-csrf - __Host- ist dort browserseitig unmoeglich.
  const csrf = typeof document === 'undefined'
    ? null
    : document.cookie.split('; ')
      .find((item) => item.startsWith('__Host-agentstudio-csrf=') || item.startsWith('agentstudio-csrf='))
      ?.split('=', 2)[1];
  const secured = SAFE_METHODS.has(req.method.toUpperCase())
    ? req.clone({ withCredentials: true })
    : req.clone({
      withCredentials: true,
      setHeaders: csrf ? { 'X-CSRF-Token': decodeURIComponent(csrf) } : {},
    });
  return next(secured).pipe(catchError((error: unknown) => {
    if (error instanceof HttpErrorResponse
        && error.status === 401
        && !req.url.endsWith('/api/v1/studio/auth/login')) {
      auth.expireNetworkedSession();
    }
    return throwError(() => error);
  }));
};
