import { AfterViewInit, Directive, ElementRef, Injector, OnDestroy, afterNextRender, inject } from '@angular/core';
import { FocusTrap, FocusTrapFactory } from '@angular/cdk/a11y';

/**
 * Keyboard contract of an `aria-modal` surface (AGT-3831).
 *
 * - Tab and Shift+Tab cycle inside the host (CDK focus-trap anchors).
 * - On open, focus moves into the host unless the owner already put it
 *   there: the host itself when it carries a `tabindex`, else its first
 *   tabbable control, else the host made focusable.
 * - On close, focus returns to the element that had it when the surface
 *   opened, unless the owner already moved focus somewhere outside.
 *
 * Escape stays with the owner and `ModalStackService`; this directive only
 * owns where focus is.
 */
@Directive({
  selector: '[appModalFocus]',
  standalone: true,
})
export class ModalFocusDirective implements AfterViewInit, OnDestroy {
  private readonly host = inject(ElementRef<HTMLElement>).nativeElement as HTMLElement;
  private readonly injector = inject(Injector);
  private readonly trap: FocusTrap | null = typeof document === 'undefined'
    ? null
    : inject(FocusTrapFactory).create(this.host, true);
  private readonly opener = focusedElementOutside(this.host);

  ngAfterViewInit(): void {
    this.trap?.attachAnchors();
    afterNextRender(() => {
      if (this.host.contains(document.activeElement)) return;
      if (this.host.hasAttribute('tabindex') || !this.trap?.focusFirstTabbableElement()) {
        // A surface without a tabbable control still takes focus, so Tab and
        // screen readers start inside it rather than on the page behind.
        if (!this.host.hasAttribute('tabindex')) this.host.tabIndex = -1;
        this.host.focus({ preventScroll: true });
      }
    }, { injector: this.injector });
  }

  ngOnDestroy(): void {
    this.trap?.destroy();
    returnFocusToOpener(this.opener, this.host);
  }
}

/** The opener a closing surface last refocused, until it loses that focus. */
let returnedFocus: HTMLElement | null = null;

/**
 * True while `element` holds focus that a closing surface handed back to its
 * opener and the user has not moved since. A follow-up surface, such as the
 * card a create dialog just made, may take that focus over (AGT-3834).
 */
export function holdsReturnedFocus(element: Element | null): boolean {
  return element !== null && element === returnedFocus && document.activeElement === element;
}

/** The focused element, or null when nothing outside `surface` has focus. */
export function focusedElementOutside(surface: HTMLElement): HTMLElement | null {
  if (typeof document === 'undefined') return null;
  const active = document.activeElement as HTMLElement | null;
  return active && active !== document.body && !surface.contains(active) ? active : null;
}

/**
 * Moves focus back to `opener` when focus would otherwise be lost: it sits
 * on the body, inside the closing `surface`, or on a removed node. A focus
 * the owner moved elsewhere on purpose is kept.
 */
export function returnFocusToOpener(opener: HTMLElement | null, surface: HTMLElement): void {
  if (!opener?.isConnected) return;
  const active = document.activeElement as HTMLElement | null;
  if (active === opener) return;
  if (active && active !== document.body && active.isConnected && !surface.contains(active)) return;
  opener.focus({ preventScroll: true });
  if (document.activeElement !== opener) return;
  returnedFocus = opener;
  opener.addEventListener('blur', () => { if (returnedFocus === opener) returnedFocus = null; }, { once: true });
}
