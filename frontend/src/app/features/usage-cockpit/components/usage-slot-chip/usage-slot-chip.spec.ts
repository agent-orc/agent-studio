import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';

import { UsageSlotChipComponent } from './usage-slot-chip';

describe('UsageSlotChipComponent', () => {
  it('is an in-place disclosure with the shared marker first', async () => {
    await TestBed.configureTestingModule({
      imports: [UsageSlotChipComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(UsageSlotChipComponent);
    fixture.componentRef.setInput('slots', [
      { name: 'remote', occupied: 2, capacity: 3, availability: { status: 'complete', observedAt: null, ttlSeconds: null } },
    ]);
    fixture.componentRef.setInput('expanded', false);
    fixture.detectChanges();
    await fixture.whenStable();
    const button = (fixture.nativeElement as HTMLElement).querySelector('button')!;
    expect(button.firstElementChild?.tagName.toLowerCase()).toBe('app-disclosure-marker');
    expect(button.getAttribute('aria-expanded')).toBe('false');
    expect(button.hasAttribute('aria-haspopup')).toBe(false);
  });
});
