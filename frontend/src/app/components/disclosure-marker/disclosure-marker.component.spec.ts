import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { DisclosureMarkerComponent } from './disclosure-marker.component';

async function build(open: boolean) {
  await TestBed.configureTestingModule({
    imports: [DisclosureMarkerComponent],
    providers: [provideZonelessChangeDetection()],
  }).compileComponents();
  const fixture = TestBed.createComponent(DisclosureMarkerComponent);
  fixture.componentRef.setInput('open', open);
  fixture.detectChanges();
  return fixture;
}

describe('DisclosureMarkerComponent', () => {
  it('carries the shared marker class and hides itself from assistive tech', async () => {
    const fixture = await build(false);
    const host = fixture.nativeElement as HTMLElement;
    expect(host.classList.contains('studio-disclosure__marker')).toBe(true);
    expect(host.getAttribute('aria-hidden')).toBe('true');
    expect(host.classList.contains('studio-disclosure__marker--open')).toBe(false);
    expect(host.getAttribute('data-open')).toBe('false');
  });

  it('marks the open state with the rotation class, not a second glyph', async () => {
    const fixture = await build(true);
    const host = fixture.nativeElement as HTMLElement;
    expect(host.classList.contains('studio-disclosure__marker--open')).toBe(true);
    expect(host.getAttribute('data-open')).toBe('true');
    // One chevron shape in both states - the open look is a CSS rotation.
    expect(host.querySelectorAll('svg')).toHaveLength(1);
  });
});
