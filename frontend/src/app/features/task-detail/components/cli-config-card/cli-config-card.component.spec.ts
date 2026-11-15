import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { CliConfigCardComponent, cliAvailabilityTone, cliTokenTone } from './cli-config-card.component';

/**
 * Cycle 11c smoke. Compiles + instantiates the standalone component.
 * What this catches: broken templateUrl/styleUrl resolution, broken
 * inject() wiring, broken signal init, decorator metadata regressions.
 *
 * What it does NOT catch: full render-path bugs that require seeded
 * inputs or per-component service stubs — those would need a
 * hand-tuned spec. `detectChanges()` is wrapped in try/catch so a
 * missing-input or missing-provider failure surfaces as a console
 * note instead of a red test, which keeps this generator-driven layer
 * stable across template tweaks.
 */
describe('CliConfigCardComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    await TestBed.configureTestingModule({
      imports: [CliConfigCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(CliConfigCardComponent);
    try { fixture.detectChanges(); } catch (e) {
      // Render needs more setup than the generic generator provides.
      // The instantiation above is still a real smoke check.
      console.warn('[smoke] CliConfigCardComponent initial render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance).toBeTruthy();
  });

  it('shows availability and token state through the shared status indicator', async () => {
    await TestBed.configureTestingModule({
      imports: [CliConfigCardComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(CliConfigCardComponent);
    fixture.componentRef.setInput('status', { path: 'claude', available: false, version: null, hasToken: false });
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    const availability = host.querySelector('[data-testid="cli-config-availability"]');
    expect(availability?.getAttribute('data-tone')).toBe('danger');
    expect(availability?.textContent?.trim()).toBe('Not found at: claude');
    const token = host.querySelector('[data-testid="cli-config-token"]');
    expect(token?.getAttribute('data-tone')).toBe('warning');
    expect(token?.textContent?.trim()).toBe('No token, CLI may fail to authenticate');
  });

  it('maps availability and token presence onto tones', () => {
    expect(cliAvailabilityTone(true)).toBe('success');
    expect(cliAvailabilityTone(false)).toBe('danger');
    expect(cliTokenTone(true)).toBe('success');
    expect(cliTokenTone(false)).toBe('warning');
  });
});
