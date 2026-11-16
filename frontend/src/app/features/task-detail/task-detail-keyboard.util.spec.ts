import { describe, expect, it } from 'vitest';
import {
  taskDetailPrimaryShortcutAllowed,
  taskDetailShortcutTargetAllowed,
  taskNavigationOwnsFocus,
} from './task-detail-keyboard.util';

function keydown(target: HTMLElement, key = 'ArrowDown'): KeyboardEvent {
  const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true });
  target.dispatchEvent(event);
  return event;
}

describe('task detail keyboard ownership', () => {
  it('gives arrow paging to task navigation surfaces', () => {
    const taskNav = document.createElement('aside');
    taskNav.className = 'task-nav';
    const task = document.createElement('button');
    taskNav.appendChild(task);

    expect(taskNavigationOwnsFocus(keydown(task))).toBe(true);
  });

  it('does not give arrow paging to detail content', () => {
    const detail = document.createElement('main');
    detail.dataset['testid'] = 'studio-task';

    expect(taskNavigationOwnsFocus(keydown(detail))).toBe(false);
  });

  it('keeps editable targets and modified shortcuts excluded', () => {
    const input = document.createElement('input');
    expect(taskDetailShortcutTargetAllowed(keydown(input))).toBe(false);

    const button = document.createElement('button');
    const modified = new KeyboardEvent('keydown', { key: 'ArrowDown', ctrlKey: true });
    button.dispatchEvent(modified);
    expect(taskDetailShortcutTargetAllowed(modified)).toBe(false);
  });
});

describe('task detail primary shortcut guard (AGT-3828)', () => {
  function cardDetail(): { detail: HTMLElement; surface: HTMLElement; cleanup: () => void } {
    const detail = document.createElement('app-task-detail');
    const surface = document.createElement('div');
    surface.tabIndex = 0;
    surface.setAttribute('data-detail-keyboard-surface', '');
    detail.appendChild(surface);
    document.body.appendChild(detail);
    return { detail, surface, cleanup: () => detail.remove() };
  }

  function enterOn(target: HTMLElement, init: KeyboardEventInit = {}): KeyboardEvent {
    const event = new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true, ...init });
    target.dispatchEvent(event);
    return event;
  }

  function withRole(role: string): HTMLElement {
    const element = document.createElement('div');
    element.tabIndex = 0;
    element.setAttribute('role', role);
    return element;
  }

  const interactiveTargets: Record<string, () => HTMLElement> = {
    button: () => document.createElement('button'),
    'a[href]': () => Object.assign(document.createElement('a'), { href: '#previous-results' }),
    '[role=tab]': () => withRole('tab'),
    summary: () => document.createElement('summary'),
    '[role=menuitem]': () => withRole('menuitem'),
  };

  it('runs from the card surface itself', () => {
    const { surface, cleanup } = cardDetail();
    try {
      expect(taskDetailPrimaryShortcutAllowed(enterOn(surface))).toBe(true);
    } finally { cleanup(); }
  });

  it.each(Object.keys(interactiveTargets))('leaves Enter to a %s inside the card surface', name => {
    const { surface, cleanup } = cardDetail();
    try {
      const control = interactiveTargets[name]();
      surface.appendChild(control);
      expect(taskDetailPrimaryShortcutAllowed(enterOn(control))).toBe(false);
    } finally { cleanup(); }
  });

  it.each(Object.keys(interactiveTargets))('leaves Enter to a %s in the Studio shell', name => {
    const { cleanup } = cardDetail();
    const control = interactiveTargets[name]();
    document.body.appendChild(control);
    try {
      expect(taskDetailPrimaryShortcutAllowed(enterOn(control))).toBe(false);
    } finally { control.remove(); cleanup(); }
  });

  it('does not run from the document body or a non-surface card element', () => {
    const { detail, surface, cleanup } = cardDetail();
    const text = document.createElement('p');
    detail.appendChild(text);
    try {
      expect(taskDetailPrimaryShortcutAllowed(enterOn(document.body))).toBe(false);
      expect(taskDetailPrimaryShortcutAllowed(enterOn(text))).toBe(false);
      expect(taskDetailPrimaryShortcutAllowed(enterOn(surface))).toBe(true);
    } finally { cleanup(); }
  });

  it.each([
    ['dialog', '[role=dialog]', (el: HTMLElement) => el.setAttribute('role', 'dialog')],
    ['alert dialog', '[role=alertdialog]', (el: HTMLElement) => el.setAttribute('role', 'alertdialog')],
    ['modal', '[aria-modal=true]', (el: HTMLElement) => el.setAttribute('aria-modal', 'true')],
    ['menu', '[role=menu]', (el: HTMLElement) => el.setAttribute('role', 'menu')],
  ])('never runs while a %s (%s) is open', (_name, _selector, mark) => {
    const { surface, cleanup } = cardDetail();
    const overlay = document.createElement('div');
    mark(overlay);
    document.body.appendChild(overlay);
    try {
      expect(taskDetailPrimaryShortcutAllowed(enterOn(surface))).toBe(false);
    } finally { overlay.remove(); cleanup(); }
  });

  it('ignores modified, repeated and already handled Enter presses', () => {
    const { surface, cleanup } = cardDetail();
    try {
      expect(taskDetailPrimaryShortcutAllowed(enterOn(surface, { shiftKey: true }))).toBe(false);
      expect(taskDetailPrimaryShortcutAllowed(enterOn(surface, { ctrlKey: true }))).toBe(false);
      expect(taskDetailPrimaryShortcutAllowed(enterOn(surface, { repeat: true }))).toBe(false);
      surface.addEventListener('keydown', event => event.preventDefault(), { once: true });
      expect(taskDetailPrimaryShortcutAllowed(enterOn(surface))).toBe(false);
      expect(taskDetailPrimaryShortcutAllowed(enterOn(surface, { key: 'j' }))).toBe(false);
    } finally { cleanup(); }
  });
});
