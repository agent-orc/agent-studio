import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { CountBadgeComponent } from './count-badge.component';

describe('CountBadgeComponent', () => {
  async function mount() {
    await TestBed.configureTestingModule({
      imports: [CountBadgeComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    return TestBed.createComponent(CountBadgeComponent);
  }

  it('renders the value inside a .count-badge pill', async () => {
    const fixture = await mount();
    fixture.componentRef.setInput('value', 42);
    fixture.detectChanges();
    const badge = fixture.nativeElement.querySelector('.count-badge');
    expect(badge?.textContent?.trim()).toBe('42');
  });

  it('renders 0 (not blank) — only null suppresses the pill', async () => {
    const fixture = await mount();
    fixture.componentRef.setInput('value', 0);
    fixture.detectChanges();
    const badge = fixture.nativeElement.querySelector('.count-badge');
    expect(badge?.textContent?.trim()).toBe('0');
  });

  it('renders nothing when value is null', async () => {
    const fixture = await mount();
    fixture.componentRef.setInput('value', null);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.count-badge')).toBeNull();
  });

  it('adds the active modifier when tone="active"', async () => {
    const fixture = await mount();
    fixture.componentRef.setInput('value', 3);
    fixture.componentRef.setInput('tone', 'active');
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.count-badge--active')).toBeTruthy();
  });

  it('adds the pane-tab variant without changing the active tone contract', async () => {
    const fixture = await mount();
    fixture.componentRef.setInput('value', 6);
    fixture.componentRef.setInput('variant', 'pane-tab');
    fixture.componentRef.setInput('tone', 'active');
    fixture.detectChanges();
    const badge = fixture.nativeElement.querySelector('.count-badge');
    expect(badge?.classList.contains('count-badge--pane-tab')).toBe(true);
    expect(badge?.classList.contains('count-badge--active')).toBe(true);
  });

  it.each([4, 12])('centres the %i pane-tab count with tabular numerals', async (value) => {
    const fixture = await mount();
    fixture.componentRef.setInput('value', value);
    fixture.componentRef.setInput('variant', 'pane-tab');
    fixture.detectChanges();
    const badge = fixture.nativeElement.querySelector('.count-badge') as HTMLElement;
    const style = getComputedStyle(badge);
    expect(style.display).toBe('inline-flex');
    expect(style.alignItems).toBe('center');
    expect(style.justifyContent).toBe('center');
    expect(style.fontVariantNumeric).toContain('tabular-nums');
  });
});
