import { DestroyRef, Directive, ElementRef, afterNextRender, inject, input } from '@angular/core';

const ITEM = '[data-roving-item]';

/**
 * Makes a group of buttons one Tab stop with arrow-key movement (AGT-3834).
 *
 * Every descendant marked `data-roving-item` takes part. Exactly one enabled
 * item keeps `tabindex="0"`: the focused item while focus is inside, else the
 * first pressed or checked item, else the one last focused, else the first
 * enabled item. The others get `tabindex="-1"`.
 * Arrow keys move focus (wrapping), Home and End jump to the ends. Arrows
 * only move focus; Space and Enter still activate the focused button, so a
 * group of pick-one or pick-many toggles keeps its `aria-pressed` semantics.
 *
 * `appRovingFocus` set to `false` leaves the items alone, so an enclosing
 * group (for example a toolbar around a segmented control and a switch) can
 * own the roving set instead.
 */
@Directive({
  selector: '[appRovingFocus]',
  standalone: true,
  host: {
    '(keydown)': 'onKeydown($event)',
    '(focusin)': 'onFocusin($event)',
    '(focusout)': 'onFocusout($event)',
  },
})
export class RovingFocusDirective {
  readonly appRovingFocus = input<boolean | ''>(true);

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private current: HTMLElement | null = null;

  constructor() {
    const destroyRef = inject(DestroyRef);
    afterNextRender(() => {
      this.sync();
      if (typeof MutationObserver === 'undefined') return;
      // Items arrive late (tag registry) or toggle `disabled`; keep one stop.
      const observer = new MutationObserver(() => this.sync());
      observer.observe(this.host.nativeElement, {
        childList: true, subtree: true, attributes: true,
        attributeFilter: ['disabled', 'aria-pressed', 'aria-checked'],
      });
      destroyRef.onDestroy(() => observer.disconnect());
    });
  }

  private enabled(): boolean {
    return this.appRovingFocus() !== false;
  }

  items(): HTMLElement[] {
    return Array.from(this.host.nativeElement.querySelectorAll<HTMLElement>(ITEM))
      .filter((item) => !(item as HTMLButtonElement).disabled);
  }

  /** Re-assign the single Tab stop. Public so specs can drive it without a render. */
  sync(leaving = false): void {
    if (!this.enabled()) return;
    const items = this.items();
    const focused = leaving ? undefined : items.find((item) => item === document.activeElement);
    const stop = focused
      ?? items.find((item) => item.getAttribute('aria-pressed') === 'true' || item.getAttribute('aria-checked') === 'true')
      ?? (this.current && items.includes(this.current) ? this.current : null)
      ?? items[0];
    for (const item of this.host.nativeElement.querySelectorAll<HTMLElement>(ITEM)) {
      const tabIndex = item === stop ? '0' : '-1';
      if (item.getAttribute('tabindex') !== tabIndex) item.setAttribute('tabindex', tabIndex);
    }
  }

  onFocusin(event: FocusEvent): void {
    if (!this.enabled()) return;
    const item = (event.target as HTMLElement | null)?.closest<HTMLElement>(ITEM);
    if (!item || !this.host.nativeElement.contains(item)) return;
    this.current = item;
    this.sync();
  }

  /** Leaving the group hands the stop back to the selected item. */
  onFocusout(event: FocusEvent): void {
    const next = event.relatedTarget as Node | null;
    if (next && this.host.nativeElement.contains(next)) return;
    this.sync(true);
  }

  onKeydown(event: KeyboardEvent): void {
    if (!this.enabled() || event.altKey || event.ctrlKey || event.metaKey) return;
    const items = this.items();
    const at = items.indexOf(event.target as HTMLElement);
    if (at < 0) return;
    let next: number;
    switch (event.key) {
      case 'ArrowRight': case 'ArrowDown': next = (at + 1) % items.length; break;
      case 'ArrowLeft': case 'ArrowUp': next = (at - 1 + items.length) % items.length; break;
      case 'Home': next = 0; break;
      case 'End': next = items.length - 1; break;
      default: return;
    }
    event.preventDefault();
    event.stopPropagation();
    items[next].focus();
  }
}
