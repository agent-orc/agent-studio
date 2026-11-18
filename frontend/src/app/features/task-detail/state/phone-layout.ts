import { DestroyRef, inject, signal, type Signal } from '@angular/core';

/** The width below which the task detail renders its full-width phone primary. */
export const TASK_PHONE_QUERY = '(max-width: 599px)';

/**
 * AGT-3830: whether the task detail uses its phone layout, kept current as
 * the viewport changes. The toolbar and the phone primary each render the
 * acceptance refusal only in their own layout, so one refusal (with its
 * test ids and focus target) exists at a time instead of a CSS-hidden twin.
 * Call it in an injection context.
 */
export function taskPhoneLayout(): Signal<boolean> {
  const media = typeof matchMedia === 'function' ? matchMedia(TASK_PHONE_QUERY) : null;
  const phone = signal(media?.matches ?? false);
  if (media) {
    const update = (event: MediaQueryListEvent) => phone.set(event.matches);
    media.addEventListener('change', update);
    inject(DestroyRef).onDestroy(() => media.removeEventListener('change', update));
  }
  return phone.asReadonly();
}
