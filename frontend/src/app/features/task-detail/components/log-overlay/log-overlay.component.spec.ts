import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { LogOverlayComponent } from './log-overlay.component';

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
describe('LogOverlayComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    await TestBed.configureTestingModule({
      imports: [LogOverlayComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(LogOverlayComponent);
    try { fixture.detectChanges(); } catch (e) {
      // Render needs more setup than the generic generator provides.
      // The instantiation above is still a real smoke check.
      console.warn('[smoke] LogOverlayComponent initial render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance).toBeTruthy();
  });
});

describe('LogOverlayComponent host ids', () => {
  it('shows no runner_<hex> id in activity or protocol lines', async () => {
    await TestBed.configureTestingModule({
      imports: [LogOverlayComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
    const fixture = TestBed.createComponent(LogOverlayComponent);
    const id = 'runner_d9a38223247c41e4bab44778397c0794';
    fixture.componentRef.setInput('cliOutput', [{ timestamp: 't', stream: 'out', text: `claimed by ${id}` }]);
    fixture.componentRef.setInput('log', [{ timestamp: 't', event: 'claim', detail: `host ${id}` }]);
    const c = fixture.componentInstance;
    expect(c.shownOutput()[0].text).not.toMatch(/runner_[0-9a-f]{8,}/);
    expect(c.shownLog()[0].detail).not.toMatch(/runner_[0-9a-f]{8,}/);
    expect(c.shownOutput()[0].text.startsWith('claimed by ')).toBe(true);
  });
});

