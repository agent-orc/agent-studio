import { AfterViewInit, Directive, ElementRef, HostListener, inject, input } from '@angular/core';

const ARROW_KEY_STEP_PX = 48;
const VIEWPORT_KEYS = new Set(['ArrowUp', 'ArrowDown', 'PageUp', 'PageDown', 'Home', 'End']);

const KEY_OWNERS = [
  'textarea',
  'input',
  'select',
  'option',
  '[contenteditable="true"]',
  '[contenteditable=""]',
  '[role="menu"]',
  '[role="menuitem"]',
  '[role="listbox"]',
  '[role="option"]',
  '[role="combobox"]',
  '[role="tree"]',
  '[role="treegrid"]',
  '[role="grid"]',
  '[role="slider"]',
  '[role="spinbutton"]',
].join(',');

/**
 * Keeps vertical viewport keys inside a focused task-detail tab surface.
 *
 * This mirrors the CAC-22 chat embedding contract: the surface owns scrolling,
 * editable and composite controls retain their native arrow behavior, and the
 * event never reaches the task pager at the document boundary.
 */
@Directive({
  selector: '[appDetailKeyboardSurface]',
  standalone: true,
  host: {
    tabindex: '0',
  },
})
export class DetailKeyboardSurfaceDirective implements AfterViewInit {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  /** Optional active descendant that owns scrolling instead of the host. */
  readonly detailKeyboardScrollTarget = input<string | null>(null);
  /** Moves focus from the board into the preferred detail surface on mount. */
  readonly detailKeyboardInitialFocus = input(false);

  ngAfterViewInit(): void {
    if (!this.detailKeyboardInitialFocus()) return;
    queueMicrotask(() => {
      const host = this.host.nativeElement;
      const detail = host.closest('.detail__panes, app-task-detail, app-job-detail') ?? host.parentElement;
      const prompt = detail?.querySelector<HTMLElement>('[data-testid="pane-prompt-body"]') ?? null;
      const protocol = detail?.querySelector<HTMLElement>('[data-testid="pane-protocol-body"]') ?? null;
      const promptOwnsRoute = prompt?.dataset['activeTab'] === 'timeline'
        || prompt?.dataset['activeTab'] === 'evidence';
      const isVisible = (candidate: HTMLElement | null): boolean => {
        for (let node: HTMLElement | null = candidate; node && node !== detail; node = node.parentElement) {
          if (node.hidden || node.style.display === 'none' || getComputedStyle(node).display === 'none') return false;
        }
        return candidate !== null;
      };
      const preferred = promptOwnsRoute && isVisible(prompt) ? prompt : (isVisible(protocol) ? protocol : prompt);
      if (preferred === host) this.focus();
    });
  }

  focus(): void {
    this.host.nativeElement.focus({ preventScroll: true });
  }

  @HostListener('keydown', ['$event'])
  onKeydown(event: KeyboardEvent): void {
    if (!VIEWPORT_KEYS.has(event.key)) return;

    const host = this.host.nativeElement;
    const target = event.target;
    if (!(target instanceof Node) || !host.contains(target)) return;

    if (this.isOwnedByFocusableControl(target, host)) {
      event.stopPropagation();
      return;
    }

    const container = this.resolveScrollContainer(host);
    if (event.defaultPrevented) {
      event.stopPropagation();
      return;
    }

    const maxScrollTop = Math.max(0, container.scrollHeight - container.clientHeight);
    container.scrollTop = this.nextScrollTop(event.key, container, maxScrollTop);
    event.preventDefault();
    event.stopPropagation();
  }

  private resolveScrollContainer(host: HTMLElement): HTMLElement {
    const selector = this.detailKeyboardScrollTarget();
    const nested = selector ? host.querySelector<HTMLElement>(selector) : null;
    if (nested) return nested;

    let element: HTMLElement | null = host;
    while (element) {
      const overflowY = getComputedStyle(element).overflowY;
      if (overflowY === 'auto' || overflowY === 'scroll' || overflowY === 'overlay') {
        return element;
      }
      element = element.parentElement;
    }
    return host;
  }

  private nextScrollTop(key: string, container: HTMLElement, maxScrollTop: number): number {
    switch (key) {
      case 'ArrowUp':
        return Math.max(0, container.scrollTop - ARROW_KEY_STEP_PX);
      case 'ArrowDown':
        return Math.min(maxScrollTop, container.scrollTop + ARROW_KEY_STEP_PX);
      case 'PageUp':
        return Math.max(0, container.scrollTop - container.clientHeight);
      case 'PageDown':
        return Math.min(maxScrollTop, container.scrollTop + container.clientHeight);
      case 'Home':
        return 0;
      case 'End':
        return maxScrollTop;
      default:
        return container.scrollTop;
    }
  }

  private isOwnedByFocusableControl(target: Node, host: HTMLElement): boolean {
    if (!(target instanceof HTMLElement)) return false;
    const owner = target.closest(KEY_OWNERS);
    return owner !== null && host.contains(owner);
  }
}
