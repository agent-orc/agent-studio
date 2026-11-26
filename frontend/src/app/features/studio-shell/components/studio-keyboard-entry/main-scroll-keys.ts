const SCROLL_KEYS = new Set(['ArrowUp', 'ArrowDown', 'PageUp', 'PageDown', 'Home', 'End', ' ']);
const ARROW_STEP_PX = 48;
const OPEN_OVERLAY_SELECTOR = 'dialog[open], [role="dialog"], [role="alertdialog"], [aria-modal="true"], [role="menu"]';

function isVisible(element: Element): boolean {
  return element instanceof HTMLElement && element.checkVisibility() && element.clientHeight > 0;
}

function canScroll(element: HTMLElement): boolean {
  const overflowY = getComputedStyle(element).overflowY;
  return (overflowY === 'auto' || overflowY === 'scroll') && element.scrollHeight > element.clientHeight + 1;
}

/**
 * The scroll container the viewport keys act on: the largest visible scrollable
 * region inside the shell's main region. The document itself does not scroll in
 * the studio shell, so with nothing focused the browser has nothing to scroll.
 */
export function mainScrollContainer(main: HTMLElement): HTMLElement | null {
  let best: HTMLElement | null = null;
  let bestArea = 0;
  for (const element of [main, ...Array.from(main.querySelectorAll<HTMLElement>('*'))]) {
    if (!canScroll(element) || !isVisible(element)) continue;
    const area = element.clientHeight * element.clientWidth;
    if (area > bestArea) { best = element; bestArea = area; }
  }
  return best;
}

/** True when no control owns the key: focus rests on the page itself or on the main region. */
function nothingOwnsKey(event: KeyboardEvent, main: HTMLElement): boolean {
  const target = event.target;
  if (!(target instanceof Element)) return true;
  if (target !== main && target !== document.body && target !== document.documentElement) return false;
  return !Array.from(document.querySelectorAll(OPEN_OVERLAY_SELECTOR)).some(isVisible);
}

/**
 * Scrolls the main scroll container for a vertical viewport key that no
 * control or view shortcut took. Returns true when it scrolled.
 */
export function scrollMainOnKey(event: KeyboardEvent, main: HTMLElement | null): boolean {
  if (!main || event.defaultPrevented || event.altKey || event.ctrlKey || event.metaKey) return false;
  if (!SCROLL_KEYS.has(event.key) || (event.shiftKey && event.key !== ' ')) return false;
  if (!nothingOwnsKey(event, main)) return false;
  const container = mainScrollContainer(main);
  if (!container) return false;
  const max = container.scrollHeight - container.clientHeight;
  const page = Math.max(ARROW_STEP_PX, container.clientHeight * 0.9);
  const top = container.scrollTop;
  let next: number;
  switch (event.key) {
    case 'ArrowDown': next = top + ARROW_STEP_PX; break;
    case 'ArrowUp': next = top - ARROW_STEP_PX; break;
    case 'PageDown': next = top + page; break;
    case 'PageUp': next = top - page; break;
    case ' ': next = event.shiftKey ? top - page : top + page; break;
    case 'Home': next = 0; break;
    default: next = max;
  }
  next = Math.min(max, Math.max(0, next));
  if (next === top) return false;
  container.scrollTop = next;
  event.preventDefault();
  return true;
}
