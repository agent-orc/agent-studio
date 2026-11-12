import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { EMPTY } from 'rxjs';
import { ShellAccessService } from './shell-access.service';

/** Stop a late-mounted background widget before a denied read reaches the server. */
export const shellReadInterceptor: HttpInterceptorFn = (req, next) => {
  if ((req.method === 'GET' || req.method === 'HEAD')
      && req.url.startsWith('/api/') && !inject(ShellAccessService).canReadBackground(req.url)) {
    return EMPTY;
  }
  return next(req);
};
