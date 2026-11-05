import { TestBed } from '@angular/core/testing';
import { ModalStackService } from './modal-stack.service';

function pressEscape(): KeyboardEvent {
  const event = new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true });
  document.dispatchEvent(event);
  return event;
}

describe('ModalStackService', () => {
  let service: ModalStackService;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [ModalStackService] });
    service = TestBed.inject(ModalStackService);
    service.clearForTest();
  });

  it('does nothing when the stack is empty', () => {
    const event = pressEscape();
    expect(event.defaultPrevented).toBe(false);
    expect(service.hasOpen()).toBe(false);
  });

  it('closes only the topmost entry on Escape', () => {
    const calls: string[] = [];
    service.push('detail', () => { calls.push('detail'); });
    service.push('add-task', () => { calls.push('add-task'); });

    const event = pressEscape();

    expect(calls).toEqual(['add-task']);
    expect(event.defaultPrevented).toBe(true);
    expect(service.depth()).toBe(2);
  });

  it('after the top entry disposes, Escape closes the next entry below', () => {
    const calls: string[] = [];
    service.push('detail', () => { calls.push('detail'); });
    const disposeAdd = service.push('add-task', () => { calls.push('add-task'); });

    disposeAdd();
    expect(service.topId()).toBe('detail');

    const event = pressEscape();
    expect(calls).toEqual(['detail']);
    expect(event.defaultPrevented).toBe(true);
  });

  it('disposer is idempotent', () => {
    const calls: string[] = [];
    const dispose = service.push('a', () => { calls.push('a'); });
    dispose();
    dispose();
    expect(service.depth()).toBe(0);
  });

  it('ignores Escape with modifier keys', () => {
    let closed = false;
    service.push('a', () => { closed = true; });

    const event = new KeyboardEvent('keydown', { key: 'Escape', ctrlKey: true, bubbles: true, cancelable: true });
    document.dispatchEvent(event);

    expect(closed).toBe(false);
    expect(event.defaultPrevented).toBe(false);
  });

  it('ignores non-Escape keys', () => {
    let closed = false;
    service.push('a', () => { closed = true; });

    const event = new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true });
    document.dispatchEvent(event);

    expect(closed).toBe(false);
    expect(event.defaultPrevented).toBe(false);
  });

  it('a handler that returns false declines and lets propagation continue', () => {
    let topClosed = false;
    let underClosed = false;
    service.push('under', () => { underClosed = true; });
    service.push('declining', () => { topClosed = true; return false; });

    const event = pressEscape();
    expect(topClosed).toBe(true);
    expect(underClosed).toBe(false);
    expect(event.defaultPrevented).toBe(false);
  });

  it('a faulty close handler does not lock the stack', () => {
    const calls: string[] = [];
    service.push('a', () => { calls.push('a'); });
    service.push('b-bad', () => { throw new Error('boom'); });

    pressEscape();
    expect(calls).toEqual([]);
    expect(service.depth()).toBe(2);

    // Bad handler stayed on the stack; the caller is expected to dispose it.
    // Subsequent Escape will retry on the same top entry — the service does
    // not auto-pop, the owning component must.
  });

  it('makes the background inert only while a modal is registered', () => {
    const page = document.createElement('main');
    page.inert = false;
    const host = document.createElement('div');
    const dialog = document.createElement('section');
    host.append(dialog);
    document.body.append(page, host);
    try {
      const dispose = service.push('modal', () => undefined, {
        dialog: () => dialog,
        backgroundRoot: () => host,
      });
      expect(page.inert).toBe(true);
      dispose();
      expect(page.inert).toBe(false);
    } finally {
      page.remove();
      host.remove();
    }
  });

  it('keeps a lower modal active when a nonmodal overlay is on top', () => {
    const page = document.createElement('main');
    page.inert = false;
    const pageButton = document.createElement('button');
    page.append(pageButton);
    const host = document.createElement('div');
    const dialog = document.createElement('section');
    const dialogButton = document.createElement('button');
    dialog.append(dialogButton);
    Object.defineProperty(dialogButton, 'getClientRects', { value: () => [new DOMRect(0, 0, 1, 1)] });
    host.append(dialog);
    document.body.append(page, host);
    try {
      service.push('modal', () => undefined, { dialog: () => dialog, backgroundRoot: () => host });
      expect(page.inert).toBe(true);
      const disposeOverlay = service.push('higher-overlay', () => undefined);
      expect(page.inert).toBe(true);
      pageButton.focus();
      expect(document.activeElement).toBe(dialogButton);
      disposeOverlay();
      expect(page.inert).toBe(true);
    } finally {
      service.clearForTest();
      page.remove();
      host.remove();
    }
  });

  it('keeps a portaled child interactive while the rest of the page stays inert', () => {
    const page = document.createElement('main');
    const host = document.createElement('div');
    const dialog = document.createElement('section');
    const portal = document.createElement('div');
    const menu = document.createElement('div');
    const otherOverlay = document.createElement('div');
    const menuButton = document.createElement('button');
    menu.append(menuButton);
    portal.append(menu, otherOverlay);
    host.append(dialog);
    document.body.append(page, host, portal);
    try {
      service.push('modal', () => undefined, { dialog: () => dialog, backgroundRoot: () => host });
      service.push('menu', () => undefined, { interactiveRoot: () => menu });
      expect(page.inert).toBe(true);
      expect(menu.inert).not.toBe(true);
      expect(portal.inert).not.toBe(true);
      expect(otherOverlay.inert).toBe(true);
      menuButton.focus();
      expect(document.activeElement).toBe(menuButton);
    } finally {
      service.clearForTest();
      page.remove();
      host.remove();
      portal.remove();
    }
  });

  it('wraps Tab and Shift+Tab at the modal boundaries', () => {
    const dialog = document.createElement('section');
    const first = document.createElement('button');
    const last = document.createElement('button');
    dialog.append(first, last);
    document.body.append(dialog);
    for (const button of [first, last]) {
      Object.defineProperty(button, 'getClientRects', { value: () => [new DOMRect(0, 0, 1, 1)] });
    }
    try {
      service.push('modal', () => undefined, { dialog: () => dialog, backgroundRoot: () => dialog });
      last.focus();
      const forward = new KeyboardEvent('keydown', { key: 'Tab', bubbles: true, cancelable: true });
      last.dispatchEvent(forward);
      expect(forward.defaultPrevented).toBe(true);
      expect(document.activeElement).toBe(first);
      const backward = new KeyboardEvent('keydown', { key: 'Tab', shiftKey: true, bubbles: true, cancelable: true });
      first.dispatchEvent(backward);
      expect(backward.defaultPrevented).toBe(true);
      expect(document.activeElement).toBe(last);
    } finally {
      dialog.remove();
    }
  });
});
