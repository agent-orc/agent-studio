const TASK_NAVIGATION_SELECTOR =
  '.task-nav, [data-testid="studio-board"], [data-testid="kanban-dashboard"]';

/** The focusable card surfaces (`DetailKeyboardSurfaceDirective` hosts). */
const CARD_SURFACE_SELECTOR = '[data-detail-keyboard-surface]';

/** Overlays that own the keyboard while they are rendered. */
const OPEN_OVERLAY_SELECTOR =
  'dialog[open], [role="dialog"], [role="alertdialog"], [aria-modal="true"], [role="menu"]';

/** The card primary action's `aria-keyshortcuts` value and accessible description. */
export const TASK_DETAIL_PRIMARY_SHORTCUT = {
  keys: 'Enter',
  description: 'Shortcut: Enter while the card content has focus.',
} as const;

/** Tooltip for the card's primary action that names where its shortcut works. */
export function taskDetailPrimaryShortcutTooltip(label: string): string {
  return `${label} (Enter in the card content)`;
}

export function taskDetailShortcutTargetAllowed(event: KeyboardEvent): boolean {
  if (event.defaultPrevented || event.metaKey || event.ctrlKey || event.altKey) return false;
  const target = event.target;
  if (!(target instanceof HTMLElement)) return true;
  if (target.isContentEditable) return false;
  return target.tagName !== 'INPUT' && target.tagName !== 'TEXTAREA' && target.tagName !== 'SELECT';
}

/**
 * Enter runs the card's primary action only while focus rests on a card
 * surface itself and no dialog or menu is open. On a button, link, tab,
 * summary, menu item or any shell control Enter keeps its native action.
 */
export function taskDetailPrimaryShortcutAllowed(event: KeyboardEvent): boolean {
  if (event.key !== 'Enter' || event.shiftKey || event.repeat || event.isComposing) return false;
  if (!taskDetailShortcutTargetAllowed(event)) return false;
  const target = event.target;
  if (!(target instanceof HTMLElement) || !target.matches(CARD_SURFACE_SELECTOR)) return false;
  return target.ownerDocument.querySelector(OPEN_OVERLAY_SELECTOR) === null;
}

export function taskNavigationOwnsFocus(event: KeyboardEvent): boolean {
  const target = event.target;
  return target instanceof Element && target.closest(TASK_NAVIGATION_SELECTOR) !== null;
}
