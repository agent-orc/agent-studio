import { ChangeDetectionStrategy, Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { DetailKeyboardSurfaceDirective } from './detail-keyboard-surface.directive';

interface ScrollState {
  scrollHeight: number;
  clientHeight: number;
  scrollTop: number;
}

function mockScrollMetrics(
  element: HTMLElement,
  initial: { scrollHeight: number; clientHeight: number },
): ScrollState {
  const state: ScrollState = { ...initial, scrollTop: 0 };
  Object.defineProperties(element, {
    scrollHeight: { configurable: true, get: () => state.scrollHeight },
    clientHeight: { configurable: true, get: () => state.clientHeight },
    scrollTop: {
      configurable: true,
      get: () => state.scrollTop,
      set: (value: number) => { state.scrollTop = value; },
    },
  });
  return state;
}

@Component({
  selector: 'app-job-detail',
  standalone: true,
  imports: [DetailKeyboardSurfaceDirective],
  templateUrl: './detail-keyboard-surface.directive.spec.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
class SurfaceHostComponent {
  initialFocus = true;
  hideProtocol = false;
}

describe('DetailKeyboardSurfaceDirective', () => {
  afterEach(() => vi.restoreAllMocks());

  function setup(): {
    root: HTMLElement;
    surface: HTMLElement;
    container: HTMLElement;
    state: ScrollState;
  } {
    const fixture = TestBed.createComponent(SurfaceHostComponent);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    const surface = root.querySelector<HTMLElement>('.surface--prompt')!;
    const container = root.querySelector<HTMLElement>('[data-scroll-owner]')!;
    const state = mockScrollMetrics(container, { scrollHeight: 1200, clientHeight: 300 });
    return { root, surface, container, state };
  }

  it('moves focus from the board to the active detail surface on open', async () => {
    const board = document.createElement('button');
    document.body.appendChild(board);
    board.focus();
    const fixture = TestBed.createComponent(SurfaceHostComponent);
    document.body.appendChild(fixture.nativeElement);
    fixture.detectChanges();
    await fixture.whenStable();

    expect(document.activeElement).toBe(
      fixture.nativeElement.querySelector('[data-testid="pane-protocol-body"]'),
    );
    fixture.nativeElement.remove();
    board.remove();
  });

  it('focuses the visible pane when the phone layout hides Result', async () => {
    const board = document.createElement('button');
    document.body.appendChild(board);
    board.focus();
    const fixture = TestBed.createComponent(SurfaceHostComponent);
    document.body.appendChild(fixture.nativeElement);
    fixture.componentInstance.hideProtocol = true;
    fixture.detectChanges();
    await fixture.whenStable();

    expect(document.activeElement).toBe(
      fixture.nativeElement.querySelector('[data-testid="pane-prompt-body"]'),
    );
    fixture.nativeElement.remove();
    board.remove();
  });

  it('scrolls arrow keys inside the focused tab without reaching board navigation', () => {
    const { root, surface, state } = setup();
    const boardNavigation = vi.fn();
    root.addEventListener('keydown', boardNavigation);
    state.scrollTop = 300;
    surface.focus();

    const down = new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true, cancelable: true });
    surface.dispatchEvent(down);
    const up = new KeyboardEvent('keydown', { key: 'ArrowUp', bubbles: true, cancelable: true });
    surface.dispatchEvent(up);

    expect(state.scrollTop).toBe(300);
    expect(down.defaultPrevented).toBe(true);
    expect(up.defaultPrevented).toBe(true);
    expect(boardNavigation).not.toHaveBeenCalled();
  });

  it('supports page and boundary scrolling from descendants of the focused tab surface', () => {
    const { surface, state } = setup();
    const action = surface.querySelector<HTMLButtonElement>('button')!;
    state.scrollTop = 300;
    action.focus();

    for (const [key, expected] of [
      ['PageDown', 600],
      ['PageUp', 300],
      ['End', 900],
      ['Home', 0],
    ] as const) {
      const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true });
      action.dispatchEvent(event);
      expect(state.scrollTop).toBe(expected);
      expect(event.defaultPrevented).toBe(true);
    }
  });

  it('contains control-owned arrows without replacing their native behavior', () => {
    const { root, surface, state } = setup();
    const boardNavigation = vi.fn();
    root.addEventListener('keydown', boardNavigation);
    state.scrollTop = 300;

    for (const key of ['ArrowDown', 'PageDown', 'End']) {
      for (const control of [
        surface.querySelector('textarea')!,
        surface.querySelector<HTMLElement>('[role="listbox"]')!,
      ]) {
        const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true });
        control.dispatchEvent(event);
        expect(event.defaultPrevented).toBe(false);
      }
    }

    expect(state.scrollTop).toBe(300);
    expect(boardNavigation).not.toHaveBeenCalled();
  });

  it('leaves Escape and Tab unchanged', () => {
    const { root, surface } = setup();
    const embeddingKeydown = vi.fn();
    root.addEventListener('keydown', embeddingKeydown);

    for (const key of ['Escape', 'Tab']) {
      const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true });
      surface.dispatchEvent(event);
      expect(event.defaultPrevented).toBe(false);
    }

    expect(embeddingKeydown).toHaveBeenCalledTimes(2);
  });

  it('leaves board-owned arrow navigation untouched', () => {
    const { root, state } = setup();
    const board = root.querySelector<HTMLButtonElement>('.board')!;
    const boardNavigation = vi.fn();
    root.addEventListener('keydown', boardNavigation);
    board.focus();

    const event = new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true, cancelable: true });
    board.dispatchEvent(event);

    expect(state.scrollTop).toBe(0);
    expect(event.defaultPrevented).toBe(false);
    expect(boardNavigation).toHaveBeenCalledOnce();
  });
});
