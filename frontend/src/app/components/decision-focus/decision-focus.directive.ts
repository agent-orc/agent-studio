import { Directive, ElementRef, Injector, afterNextRender, effect, inject, input } from '@angular/core';
import { DecisionFocusRequestService, decisionTaskKey } from '../../services/decision-focus-request.service';

/** Marks the element that holds a card's decision; carried by every `appDecisionFocus` host. */
export const DECISION_FOCUS_SELECTOR = '[data-decision-focus]';

/**
 * Where focus stays when a card mounts or asks for its decision area: that
 * area, or an open dialog such as the written-reason override a refused
 * Accept opened (AGT-3873).
 */
export const KEEPS_FOCUS_SELECTOR = `${DECISION_FOCUS_SELECTOR}, [role="dialog"]`;

/**
 * Marks a card's decision area (the lane primary and its siblings). When the
 * card was opened to be decided on, the first visible area takes focus, so the
 * primary action is the next Tab stop (AGT-3833).
 */
@Directive({
  selector: '[appDecisionFocus]',
  standalone: true,
  host: {
    'data-decision-focus': '',
    tabindex: '-1',
    '[attr.role]': 'decisionFocusLabel() ? \'group\' : null',
    '[attr.aria-label]': 'decisionFocusLabel()',
  },
})
export class DecisionFocusDirective {
  /** The card this area decides on. */
  readonly appDecisionFocus = input.required<{ taskKey?: string | null; watchPath?: string | null; id: string }>();
  /** Names the area as a group; null for a host that already has its own name and role. */
  readonly decisionFocusLabel = input<string | null>('Decision');

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly requests = inject(DecisionFocusRequestService);
  private readonly injector = inject(Injector);

  constructor() {
    effect(() => {
      this.requests.request();
      const taskKey = decisionTaskKey(this.appDecisionFocus());
      if (!this.requests.wants(taskKey)) return;
      afterNextRender(() => this.focusIfWanted(taskKey), { injector: this.injector });
    });
  }

  private focusIfWanted(taskKey: string): void {
    if (!this.requests.wants(taskKey)) return;
    const host = this.host.nativeElement;
    // A layout that hides this area (the phone tab bar, a collapsed header) leaves the request to a visible one.
    if (!host.isConnected || (typeof host.checkVisibility === 'function' && !host.checkVisibility())) return;
    if (document.activeElement?.closest(KEEPS_FOCUS_SELECTOR)) return;
    host.focus({ preventScroll: false });
  }
}
