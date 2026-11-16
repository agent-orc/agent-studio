import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideZonelessChangeDetection } from '@angular/core';
import { SegmentedControlComponent } from './segmented-control.component';
import { TooltipDirective } from 'coding-agent-chat/shared';

/**
 * Render-path spec for the shared segmented control. Pins the load-bearing
 * accessibility + selection contract the Settings toggles rely on:
 *   - one button per option, group labelled for assistive tech,
 *   - exactly the selected option carries aria-pressed="true",
 *   - clicking another option emits its value (and re-clicking the active
 *     option is a no-op).
 */
describe('SegmentedControlComponent', () => {
  function setup(value: 'dark' | 'light') {
    TestBed.configureTestingModule({
      imports: [SegmentedControlComponent],
      providers: [provideZonelessChangeDetection()],
    });
    const fixture = TestBed.createComponent(SegmentedControlComponent<'dark' | 'light'>);
    fixture.componentRef.setInput('options', [
      { value: 'dark', label: 'Dark', testid: 'opt-dark' },
      { value: 'light', label: 'Light', testid: 'opt-light' },
    ]);
    fixture.componentRef.setInput('value', value);
    fixture.componentRef.setInput('ariaLabel', 'Theme');
    fixture.detectChanges();
    return fixture;
  }

  it('renders one button per option inside a labelled group', () => {
    const fixture = setup('light');
    const group = fixture.nativeElement.querySelector('[role="group"]');
    expect(group?.getAttribute('aria-label')).toBe('Theme');
    const buttons = fixture.nativeElement.querySelectorAll('button.segmented__option');
    expect(buttons.length).toBe(2);
  });

  it('marks only the selected option as pressed/active', () => {
    const fixture = setup('light');
    const dark = fixture.nativeElement.querySelector('[data-testid="opt-dark"]');
    const light = fixture.nativeElement.querySelector('[data-testid="opt-light"]');
    expect(light.getAttribute('aria-pressed')).toBe('true');
    expect(dark.getAttribute('aria-pressed')).toBe('false');
    expect(light.classList.contains('segmented__option--active')).toBe(true);
    expect(dark.classList.contains('segmented__option--active')).toBe(false);
  });

  it('emits the value when a different option is clicked, and ignores the active one', () => {
    const fixture = setup('light');
    const emitted: string[] = [];
    fixture.componentInstance.valueChange.subscribe((v) => emitted.push(v));

    fixture.nativeElement.querySelector('[data-testid="opt-dark"]').click();
    fixture.nativeElement.querySelector('[data-testid="opt-light"]').click(); // active → no-op
    expect(emitted).toEqual(['dark']);
  });

  it('renders one track and preserves optional tooltips', () => {
    const fixture = setup('light');
    fixture.componentRef.setInput('options', [
      { value: 'dark', label: 'Dark', tooltip: 'Use dark colours' },
      { value: 'light', label: 'Light' },
    ]);
    fixture.detectChanges();
    const button = fixture.debugElement.query(By.css('button'));
    expect(button.injector.get(TooltipDirective).content()).toBe('Use dark colours');
    expect(button.nativeElement.hasAttribute('title')).toBe(false);
  });

  it('renders a studio icon only for options that declare one', () => {
    const fixture = setup('light');
    fixture.componentRef.setInput('options', [
      { value: 'dark', label: 'Dark', icon: 'moon', testid: 'opt-dark' },
      { value: 'light', label: 'Light', testid: 'opt-light' },
    ]);
    fixture.detectChanges();

    const dark = fixture.nativeElement.querySelector('[data-testid="opt-dark"]');
    expect(dark.classList.contains('segmented__option--with-icon')).toBe(true);
    expect(dark.querySelector('.segmented__icon svg'), 'icon svg').toBeTruthy();
    expect(dark.textContent).toContain('Dark');

    const light = fixture.nativeElement.querySelector('[data-testid="opt-light"]');
    expect(light.classList.contains('segmented__option--with-icon')).toBe(false);
    expect(light.querySelector('.segmented__icon')).toBeNull();
  });
});

describe('SegmentedControlComponent keyboard (AGT-3834)', () => {
  function setup(rovingFocus = true) {
    TestBed.configureTestingModule({
      imports: [SegmentedControlComponent],
      providers: [provideZonelessChangeDetection()],
    });
    const fixture = TestBed.createComponent(SegmentedControlComponent<'a' | 'b' | 'c'>);
    fixture.componentRef.setInput('options', [
      { value: 'a', label: 'A', testid: 'opt-a' },
      { value: 'b', label: 'B', testid: 'opt-b' },
      { value: 'c', label: 'C', testid: 'opt-c' },
    ]);
    fixture.componentRef.setInput('value', 'b');
    fixture.componentRef.setInput('rovingFocus', rovingFocus);
    fixture.detectChanges();
    const el = (id: string) => fixture.nativeElement.querySelector(`[data-testid="opt-${id}"]`) as HTMLButtonElement;
    return { fixture, el };
  }

  it('is one Tab stop, on the selected option', async () => {
    const { fixture, el } = setup();
    await fixture.whenStable();
    expect(['a', 'b', 'c'].map((id) => el(id).getAttribute('tabindex'))).toEqual(['-1', '0', '-1']);
  });

  it('moves focus with the arrow keys and selects with a click (Space/Enter)', async () => {
    const { fixture, el } = setup();
    await fixture.whenStable();
    const emitted: string[] = [];
    fixture.componentInstance.valueChange.subscribe((v) => emitted.push(v));
    el('b').focus();
    el('b').dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true, cancelable: true }));
    expect(document.activeElement).toBe(el('c'));
    expect(emitted).toEqual([]);
    el('c').click();
    expect(emitted).toEqual(['c']);
  });

  it('leaves Tab order to an enclosing group when rovingFocus is off', async () => {
    const { fixture, el } = setup(false);
    await fixture.whenStable();
    expect(['a', 'b', 'c'].map((id) => el(id).getAttribute('tabindex'))).toEqual([null, null, null]);
  });
});
