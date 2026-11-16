import { DestroyRef, Injector, Signal, afterNextRender, effect, inject } from '@angular/core';
import { ModalStackService } from '../../../../services/modal-stack.service';

/**
 * Keyboard focus helpers for the Dossier decision surfaces (AGT-3829). A
 * control that is replaced or removed drops focus to the document body; these
 * helpers move it to the replacement instead.
 */

const TABBABLE = 'a[href], button, input, select, textarea, summary, iframe, [tabindex]';

/** True when focus has dropped to the body, or sits inside `root` when one is given. */
export function focusLost(root?: Element): boolean {
  const active = document.activeElement;
  return !active || active === document.body || (root?.contains(active) ?? false);
}

/** Keyboard-reachable descendants of `root` in document order, excluding `root`. */
export function tabbableIn(root: Element): HTMLElement[] {
  return Array.from(root.querySelectorAll<HTMLElement>(TABBABLE)).filter(element =>
    element.tabIndex >= 0 && !element.matches(':disabled') && !element.closest('[inert]')
    && element.getAttribute('aria-hidden') !== 'true'
    && (typeof element.checkVisibility !== 'function' || element.checkVisibility()));
}

/**
 * After the next render, focus the `testId` control under `root`; when it is
 * missing or disabled, focus the first enabled control matching `fallback`.
 */
export function focusAfterRender(root: HTMLElement, injector: Injector, testId: string, fallback: string): void {
  afterNextRender(() => {
    const target = root.querySelector<HTMLElement>(`[data-testid="${testId}"]`);
    (target && !target.matches(':disabled') ? target : tabbableIn(root).find(element =>
      element.matches(fallback)))?.focus();
  }, { injector });
}

/**
 * Register an open inline form on the shared modal stack. The stack handles
 * Escape at document capture and its top entry consumes the key unless it
 * declines, so a form-level Escape handler below another entry never runs.
 * This entry declines while focus is outside the form. Call in an injection context.
 */
export function bindFormEscape(open: Signal<boolean>, form: () => Element | null, close: () => boolean): void {
  const modalStack = inject(ModalStackService);
  let release: (() => void) | null = null;
  effect(() => {
    if (open() && !release) {
      release = modalStack.push('workbench-decision-confirmation', () =>
        form()?.contains(document.activeElement) ? close() : false);
    } else if (!open() && release) {
      release();
      release = null;
    }
  });
  inject(DestroyRef).onDestroy(() => release?.());
}
