import { InjectionToken } from '@angular/core';

// Replaced by the Angular production build. Dev serves leave it undefined.
declare const STUDIO_BUILD_SHA: string;

export const FRONTEND_BUILD_SHA: string | null =
  typeof STUDIO_BUILD_SHA === 'string' && /^[0-9a-f]{40}$/.test(STUDIO_BUILD_SHA)
    ? STUDIO_BUILD_SHA
    : null;

export const BUILD_SHA = new InjectionToken<string | null>('Studio frontend build SHA', {
  providedIn: 'root',
  factory: () => FRONTEND_BUILD_SHA,
});
