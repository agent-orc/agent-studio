import { afterEach, describe, expect, it } from 'vitest';
import { scrollMainOnKey } from './main-scroll-keys';

describe('scrollMainOnKey', () => {
  afterEach(() => document.body.replaceChildren());

  function setup() {
    const main = document.createElement('div');
    main.style.cssText = 'height:100px;overflow-y:auto';
    const content = document.createElement('div');
    content.style.height = '1000px';
    main.appendChild(content);
    document.body.appendChild(main);
    return main;
  }
  const press = (key: string, target: EventTarget, init: KeyboardEventInit = {}) => {
    const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true, ...init });
    Object.defineProperty(event, 'target', { value: target });
    return event;
  };

  // Layout-dependent scrolling is pinned by e2e/project/main-scroll-keys.spec.ts; jsdom has no layout.

  it('leaves the key to a focused input, a prevented event and modified keys', () => {
    const main = setup();
    const input = document.createElement('input');
    main.appendChild(input);
    expect(scrollMainOnKey(press('ArrowDown', input), main)).toBe(false);
    const prevented = press('ArrowDown', document.body);
    prevented.preventDefault();
    expect(scrollMainOnKey(prevented, main)).toBe(false);
    expect(scrollMainOnKey(press('ArrowDown', document.body, { ctrlKey: true }), main)).toBe(false);
    expect(main.scrollTop).toBe(0);
  });
});
