import { afterEach, describe, expect, it, vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { StudioKeyboardEntryComponent } from './studio-keyboard-entry.component';

describe('StudioKeyboardEntryComponent', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
    document.querySelectorAll('[data-testid="studio-attention-trigger"]').forEach(node => node.remove());
  });

  function setup(needsYouAvailable = true) {
    TestBed.configureTestingModule({
      imports: [StudioKeyboardEntryComponent],
      providers: [provideZonelessChangeDetection()],
    });
    const main = document.createElement('div');
    main.tabIndex = -1;
    document.body.appendChild(main);
    const fixture = TestBed.createComponent(StudioKeyboardEntryComponent);
    fixture.componentRef.setInput('main', main);
    fixture.componentRef.setInput('needsYouAvailable', needsYouAvailable);
    fixture.detectChanges();
    const shortcut = vi.fn();
    fixture.componentInstance.searchShortcut.subscribe(shortcut);
    return { fixture, main, shortcut, root: fixture.nativeElement as HTMLElement };
  }

  it.each([
    ['Ctrl+K', { ctrlKey: true }],
    ['Cmd+K', { metaKey: true }],
    ['Ctrl+Shift+K', { ctrlKey: true, shiftKey: true, key: 'K' }],
  ])('emits the search shortcut for %s from anywhere and keeps the browser default off', (_name, chord) => {
    const { shortcut } = setup();
    const input = document.createElement('input');
    document.body.appendChild(input);
    const event = new KeyboardEvent('keydown', { key: 'k', bubbles: true, cancelable: true, ...chord });
    input.dispatchEvent(event);
    expect(shortcut).toHaveBeenCalledTimes(1);
    expect(event.defaultPrevented).toBe(true);
    input.remove();
  });

  it('ignores a plain k and Alt chords', () => {
    const { shortcut } = setup();
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'k' }));
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'k', ctrlKey: true, altKey: true }));
    expect(shortcut).not.toHaveBeenCalled();
  });

  it('moves focus to the main content and to Needs you', () => {
    const { root, main } = setup();
    const trigger = document.createElement('button');
    trigger.dataset['testid'] = 'studio-attention-trigger';
    document.body.appendChild(trigger);

    root.querySelector<HTMLAnchorElement>('[data-testid="studio-skip-to-main"]')!.click();
    expect(document.activeElement).toBe(main);
    root.querySelector<HTMLAnchorElement>('[data-testid="studio-skip-to-needs-you"]')!.click();
    expect(document.activeElement).toBe(trigger);
    main.remove();
  });

  it('offers no Needs you link where the session has no Needs you', () => {
    const { root, main } = setup(false);
    expect(root.querySelector('[data-testid="studio-skip-to-needs-you"]')).toBeNull();
    expect(root.querySelector('[data-testid="studio-skip-to-main"]')).not.toBeNull();
    main.remove();
  });
});
