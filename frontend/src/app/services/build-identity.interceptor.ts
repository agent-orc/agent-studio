import { HttpErrorResponse, HttpInterceptorFn, HttpResponse } from '@angular/common/http';
import { inject } from '@angular/core';
import { tap } from 'rxjs';
import { BuildUpdateNoticeService, BUILD_SHA_HEADER } from './build-update-notice.service';

export const buildIdentityInterceptor: HttpInterceptorFn = (request, next) => {
  const notice = inject(BuildUpdateNoticeService);
  if (!request.url.startsWith('/api/')) return next(request);
  return next(request).pipe(tap({
    next: event => {
      if (event instanceof HttpResponse) notice.observe(event.headers.get(BUILD_SHA_HEADER));
    },
    error: error => {
      if (error instanceof HttpErrorResponse) notice.observe(error.headers.get(BUILD_SHA_HEADER));
    },
  }));
};
