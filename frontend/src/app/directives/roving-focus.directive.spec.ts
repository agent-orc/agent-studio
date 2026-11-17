import { describe, expect, it } from 'vitest';
import { ChangeDetectionStrategy, Component, provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { RovingFocusDirective } from './roving-focus.directive';

@Component({
  standalone: true,
  imports: [RovingFocusDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './roving-focus.directive.spec.html',
})
class HostComponent {
  readonly cDisabled = signal(true);
}

function setup() {
  TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
  const fixture = TestBed.createComponent(HostComponent);
  fixture.detectChanges();
  const directive = fixture.debugElement.children[0].injector.get(RovingFocusDirective);
  directive.sync();
  const el = (id: string) => fixture.nativeElement.querySelector(`[data-testid="${id}"]`) as HTMLButtonElement;
  const stops = () => ['a', 'b', 'c', 'd'].filter((id) => el(id).getAttribute('tabindex') === '0');
  const key = (id: string, k: string) => {
    const event = new KeyboardEvent('keydown', { key: k, bubbles: true, cancelable: true });
    el(id).dispatchEvent(event);
    return event;
  };
  return { fixture, directive, el, stops, key };
}

describe('RovingFocusDirective (AGT-3834)', () => {
  it('leaves exactly one Tab stop, on the pressed item', () => {
    const { stops } = setup();
    expect(stops()).toEqual(['b']);
  });

  it('moves focus with the arrow keys, skips disabled items, and wraps', () => {
    const { el, key, stops } = setup();
    el('b').focus();
    expect(key('b', 'ArrowRight').defaultPrevented).toBe(true);
    expect(document.activeElement).toBe(el('d'));
    expect(stops()).toEqual(['d']);
    key('d', 'ArrowRight');
    expect(document.activeElement).toBe(el('a'));
    key('a', 'ArrowLeft');
    expect(document.activeElement).toBe(el('d'));
    key('d', 'Home');
    expect(document.activeElement).toBe(el('a'));
    key('a', 'End');
    expect(document.activeElement).toBe(el('d'));
  });

  it('hands the stop back to the pressed item when focus leaves the group', () => {
    const { el, key, stops } = setup();
    el('b').focus();
    key('b', 'ArrowLeft');
    expect(stops()).toEqual(['a']);
    el('outside').focus();
    expect(stops()).toEqual(['b']);
  });

  it('falls back to the first enabled item when nothing is pressed', () => {
    const { el, directive, stops } = setup();
    el('b').removeAttribute('aria-pressed');
    directive.sync();
    expect(stops()).toEqual(['a']);
  });

  it('ignores keys it does not own', () => {
    const { el, key } = setup();
    el('b').focus();
    expect(key('b', 'Enter').defaultPrevented).toBe(false);
    expect(document.activeElement).toBe(el('b'));
  });
});
