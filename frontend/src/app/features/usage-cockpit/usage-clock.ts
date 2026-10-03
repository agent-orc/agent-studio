import { DestroyRef, inject, signal, type Signal } from '@angular/core';

/** How often a chip without a host-supplied reference time re-checks staleness. */
export const USAGE_CLOCK_TICK_MS = 30_000;

/**
 * Epoch-millisecond clock for chip staleness. A TTL can elapse while a chip
 * stays on screen without a new projection, so the reference time has to
 * move on its own; the interval stops with the injecting component.
 */
export function injectUsageClock(): Signal<number> {
  const now = signal(Date.now());
  const handle = setInterval(() => now.set(Date.now()), USAGE_CLOCK_TICK_MS);
  inject(DestroyRef).onDestroy(() => clearInterval(handle));
  return now.asReadonly();
}
