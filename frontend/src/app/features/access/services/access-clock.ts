import { InjectionToken } from '@angular/core';

/** Milliseconds since the epoch for token and enrollment expiry; specs provide a fixed clock. */
export const ACCESS_CLOCK = new InjectionToken<() => number>('access clock', {
  providedIn: 'root', factory: () => Date.now,
});
