import { Directive, ElementRef, HostListener, OnDestroy, inject } from '@angular/core';
import {
  ConnectedOverlayPositionRef,
  OverlayPortalRef,
  OverlayPortalService,
} from '@studio/ui/services/overlay-portal.service';
import { ModalStackService } from '@studio/ui/services/modal-stack.service';
import { TokenPopoverCoordinator } from './token-popover-coordinator.service';

/**
 * Lifts the token-usage popover into the central body overlay layer so it can
 * never be clipped by the card.
 *
 * The card sets both `overflow: hidden` and `content-visibility: auto`
 * (native virtualisation); the latter implies paint containment, which clips
 * *every* descendant — including `position: fixed` ones — to the card's box.
 * The lane is a scroll container on top of that. An in-flow absolutely
 * positioned popover therefore gets cut off at the first card/panel edge.
 *
 * The directive routes the existing template element through
 * `OverlayPortalService`, which appends it under the shared body-level overlay
 * root. Positioning also goes through the service so flip / clamp behaviour is
 * shared with menus, model pickers, and prompt popovers.
 *
 * Apply on the wrapper that contains the trigger + the `[data-token-popover]`
 * element. The popover element MUST start with the `hidden` attribute in markup:
 * this directive only toggles `pop.hidden`, it does not own the initial state.
 * Without it every card paints its `position: fixed` popover at its static
 * position (off the right viewport edge) until it is first hovered (ASS-1700).
 */
@Directive({
  selector: '[appTokenPopover]',
  standalone: true,
})
export class TokenPopoverDirective implements OnDestroy {
  private static readonly GAP = 8;
  private static readonly VIEWPORT_PAD = 6;
  /** Match the board's hover-intent convention so passing over chips stays quiet. */
  private static readonly OPEN_DELAY_MS = 300;
  /** Grace period so the pointer can cross the trigger→popover gap without it closing. */
  private static readonly CLOSE_DELAY_MS = 120;

  private readonly host = inject(ElementRef<HTMLElement>).nativeElement;
  private readonly overlayPortal = inject(OverlayPortalService);
  private readonly modalStack = inject(ModalStackService);
  private readonly coordinator = inject(TokenPopoverCoordinator);
  private popoverEl: HTMLElement | null = null;
  private portalRef: OverlayPortalRef | null = null;
  private positionRef: ConnectedOverlayPositionRef | null = null;
  private openTimer: ReturnType<typeof setTimeout> | null = null;
  private closeTimer: ReturnType<typeof setTimeout> | null = null;
  private globalListeners: AbortController | null = null;
  private intersectionObserver: IntersectionObserver | null = null;
  private modalStackDispose: (() => void) | null = null;
  private isOpen = false;

  ngOnDestroy(): void {
    this.cancelOpen();
    this.cancelClose();
    this.close();
  }

  @HostListener('mouseenter')
  onPointerEnter(): void {
    this.scheduleOpen();
  }

  @HostListener('focusin')
  onFocusIn(): void {
    this.open();
  }

  @HostListener('mouseleave')
  @HostListener('focusout')
  onLeave(): void {
    this.cancelOpen();
    this.scheduleClose();
  }

  private open(): void {
    this.cancelOpen();
    this.cancelClose();
    const pop = this.resolvePopover();
    if (!pop) return;

    if (this.isOpen) return;
    this.coordinator.claim(this, () => this.close());
    this.isOpen = true;
    pop.hidden = false;
    pop.style.left = '-9999px';
    pop.style.top = '0px';
    this.portalRef ??= this.overlayPortal.attachPanel(pop);
    pop.addEventListener('mouseenter', this.cancelCloseBound);
    pop.addEventListener('mouseleave', this.scheduleCloseBound);
    pop.addEventListener('focusin', this.cancelCloseBound);
    pop.addEventListener('focusout', this.scheduleCloseBound);
    this.positionRef?.dispose();
    this.positionRef = this.overlayPortal.watchConnectedPosition(this.host, pop, {
      preferredPlacement: 'above',
      alignment: 'end',
      gap: TokenPopoverDirective.GAP,
      viewportPadding: TokenPopoverDirective.VIEWPORT_PAD,
    });
    this.attachDismissalBoundaries(pop);
  }

  private close(): void {
    this.cancelOpen();
    this.cancelClose();
    const pop = this.popoverEl;
    this.positionRef?.dispose();
    this.positionRef = null;
    this.intersectionObserver?.disconnect();
    this.intersectionObserver = null;
    this.globalListeners?.abort();
    this.globalListeners = null;
    this.modalStackDispose?.();
    this.modalStackDispose = null;
    if (pop) {
      pop.removeEventListener('mouseenter', this.cancelCloseBound);
      pop.removeEventListener('mouseleave', this.scheduleCloseBound);
      pop.removeEventListener('focusin', this.cancelCloseBound);
      pop.removeEventListener('focusout', this.scheduleCloseBound);
      pop.hidden = true;
    }
    this.portalRef?.dispose();
    this.portalRef = null;
    this.isOpen = false;
    this.coordinator.release(this);
  }

  private resolvePopover(): HTMLElement | null {
    if (this.popoverEl?.isConnected) return this.popoverEl;
    this.popoverEl = this.host.querySelector('[data-token-popover]');
    if (this.popoverEl) this.popoverEl.hidden = true;
    return this.popoverEl;
  }

  private scheduleOpen(): void {
    this.cancelOpen();
    this.openTimer = setTimeout(() => {
      this.openTimer = null;
      this.open();
    }, TokenPopoverDirective.OPEN_DELAY_MS);
  }

  private scheduleClose(): void {
    this.cancelClose();
    this.closeTimer = setTimeout(() => {
      this.closeTimer = null;
      this.close();
    }, TokenPopoverDirective.CLOSE_DELAY_MS);
  }

  private attachDismissalBoundaries(pop: HTMLElement): void {
    this.globalListeners?.abort();
    const listeners = new AbortController();
    this.globalListeners = listeners;

    document.addEventListener('pointerdown', this.onDocumentPointerDown, {
      capture: true,
      signal: listeners.signal,
    });

    const laneScroll = this.host.closest('[data-board-lane-scroll]') as HTMLElement | null;
    laneScroll?.addEventListener('scroll', this.closeBound, {
      passive: true,
      signal: listeners.signal,
    });

    this.modalStackDispose = this.modalStack.push('task-token-usage-popover', () => {
      this.close();
      return true;
    });

    if (typeof IntersectionObserver !== 'undefined') {
      const anchorCard = (this.host.closest('[data-token-popover-anchor-card]') as HTMLElement | null) ?? this.host;
      this.intersectionObserver = new IntersectionObserver((entries) => {
        if (entries.some(entry => entry.target === anchorCard && !entry.isIntersecting)) {
          this.close();
        }
      });
      this.intersectionObserver.observe(anchorCard);
    }

    // The panel is portaled outside the host, so the outside-click boundary
    // must explicitly include it as well as the anchor wrapper.
    this.popoverEl = pop;
  }

  private readonly onDocumentPointerDown = (event: Event): void => {
    if (!this.isOpen || !(event.target instanceof Node)) return;
    if (this.host.contains(event.target) || this.popoverEl?.contains(event.target)) return;
    this.close();
  };

  private cancelClose(): void {
    if (this.closeTimer !== null) {
      clearTimeout(this.closeTimer);
      this.closeTimer = null;
    }
  }

  private cancelOpen(): void {
    if (this.openTimer !== null) {
      clearTimeout(this.openTimer);
      this.openTimer = null;
    }
  }

  private readonly cancelCloseBound = () => this.cancelClose();
  private readonly scheduleCloseBound = () => this.scheduleClose();
  private readonly closeBound = () => this.close();
}
