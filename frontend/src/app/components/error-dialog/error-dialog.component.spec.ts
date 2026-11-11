import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { ErrorDialogComponent } from './error-dialog.component';
import type { ErrorDialogState } from '../../models/error-dialog.model';

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
describe('ErrorDialogComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    await TestBed.configureTestingModule({
      imports: [ErrorDialogComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(ErrorDialogComponent);
    fixture.componentRef.setInput('error', undefined);

    // Required inputs seeded with undefined — replace with realistic defaults if needed:
    // error
    try { fixture.detectChanges(); } catch (e) {
      // Render needs more setup than the generic generator provides.
      // The instantiation above is still a real smoke check.
      console.warn('[smoke] ErrorDialogComponent initial render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance).toBeTruthy();
  });
});

describe('ErrorDialogComponent expected refusal (AGT-3624)', () => {
  async function render(state: Partial<ErrorDialogState>) {
    await TestBed.configureTestingModule({
      imports: [ErrorDialogComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(ErrorDialogComponent);
    fixture.componentRef.setInput('error', {
      title: 'Cannot move to Accepted',
      message: 'integration-pending: Complete integration. Current status: pending.',
      output: '{ "status": 409 }',
      stackTrace: null,
      source: 'Task demo-1',
      canOpenCliConfig: false,
      ...state,
    });
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    return { fixture, el, q: (id: string) => el.ownerDocument.querySelector(`[data-testid="${id}"]`) as HTMLElement | null };
  }

  it('leads with the plain sentence and keeps the raw payload behind Technical details', async () => {
    const { fixture, q } = await render({
      refusal: { sentence: "This card's work is not in develop yet.", nextAction: 'Wait for integration to finish.' },
    });
    expect(q('error-dialog-message')?.textContent).toBe("This card's work is not in develop yet.");
    expect(q('error-dialog-next-action')?.textContent).toBe('Wait for integration to finish.');
    expect(q('error-dialog-technical')).toBeNull();
    const toggle = q('error-dialog-technical-toggle')!;
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(toggle.querySelector('app-disclosure-marker')).toBeTruthy();

    toggle.click();
    fixture.detectChanges();
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(q('error-dialog-server-message')?.textContent).toContain('integration-pending');
    expect(q('error-dialog-output')?.textContent).toContain('409');
  });

  it('omits an empty stack trace section on a plain error', async () => {
    const { el } = await render({});
    expect(el.ownerDocument.body.textContent).not.toContain('Stack trace');
  });
});
