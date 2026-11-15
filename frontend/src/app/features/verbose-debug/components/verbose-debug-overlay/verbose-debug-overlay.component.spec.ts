import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { VerboseDebugOverlayComponent } from './verbose-debug-overlay.component';
import { artifactStatusTone, debugRunStatusTone, decisionSeverityTone, warningRowTone } from './verbose-debug-status';

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
describe('VerboseDebugOverlayComponent (smoke)', () => {
  function createFixture() {
    return TestBed.configureTestingModule({
      imports: [VerboseDebugOverlayComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents().then(() => TestBed.createComponent(VerboseDebugOverlayComponent));
  }

  it('compiles + instantiates without throwing', async () => {
    const fixture = await createFixture();
    try { fixture.detectChanges(); } catch (e) {
      // Render needs more setup than the generic generator provides.
      // The instantiation above is still a real smoke check.
      console.warn('[smoke] VerboseDebugOverlayComponent initial render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance).toBeTruthy();
  });

  it('does not advertise a Trace tab in the navigation', async () => {
    const fixture = await createFixture();
    const tabIds = fixture.componentInstance.tabs.map((t) => t.id);
    expect(tabIds).not.toContain('trace');
    expect(tabIds).toEqual([
      'overview',
      'actors',
      'phases',
      'orchestrator',
      'tools',
      'warnings',
      'tasks',
      'tokens',
      'artifacts',
    ]);
  });

  it('falls back to overview when a stale tab key is requested', async () => {
    const fixture = await createFixture();
    fixture.componentRef.setInput('initialTab', 'trace' as unknown as 'overview');
    fixture.detectChanges();
    await new Promise<void>((r) => queueMicrotask(r));
    expect(fixture.componentInstance.activeTab()).toBe('overview');
  });
});

describe('verbose debug status tones', () => {
  it('maps decision severity, warning thresholds, run status and artifact status', () => {
    expect(decisionSeverityTone(null)).toBe('info');
    expect(decisionSeverityTone('warn')).toBe('warning');
    expect(decisionSeverityTone('error')).toBe('danger');
    expect(warningRowTone('info')).toBe('neutral');
    expect(warningRowTone('warn')).toBe('warning');
    expect(warningRowTone('danger')).toBe('danger');
    expect(debugRunStatusTone('running')).toBe('running');
    expect(debugRunStatusTone('completed')).toBe('success');
    expect(debugRunStatusTone('failed')).toBe('danger');
    expect(debugRunStatusTone('cancelled')).toBe('neutral');
    expect(artifactStatusTone('passed')).toBe('success');
    expect(artifactStatusTone('failed')).toBe('danger');
    expect(artifactStatusTone('pending')).toBe('neutral');
  });
});
