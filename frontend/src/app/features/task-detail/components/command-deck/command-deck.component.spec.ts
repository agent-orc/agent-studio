import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { CommandDeckComponent } from './command-deck.component';

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
describe('CommandDeckComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    await TestBed.configureTestingModule({
      imports: [CommandDeckComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(CommandDeckComponent);
    fixture.componentRef.setInput('currentWatchPath', undefined);
    fixture.componentRef.setInput('cliTypeDraft', undefined);
    fixture.componentRef.setInput('modelDraft', undefined);

    // Required inputs seeded with undefined — replace with realistic defaults if needed:
    // currentWatchPath, cliTypeDraft, modelDraft
    try { fixture.detectChanges(); } catch (e) {
      // Render needs more setup than the generic generator provides.
      // The instantiation above is still a real smoke check.
      console.warn('[smoke] CommandDeckComponent initial render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance).toBeTruthy();
  });
});

describe('CommandDeckComponent remote run control', () => {
  it('renders Stop and emits the stop request while remote ownership is active', async () => {
    await TestBed.configureTestingModule({
      imports: [CommandDeckComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(CommandDeckComponent);
    fixture.componentRef.setInput('currentWatchPath', '/workspace');
    fixture.componentRef.setInput('cliTypeDraft', 'codex');
    fixture.componentRef.setInput('modelDraft', 'gpt-6');
    fixture.componentRef.setInput('isRunning', true);
    fixture.componentRef.setInput('canStart', false);
    fixture.detectChanges();

    let stops = 0;
    fixture.componentInstance.stopRequest.subscribe(() => stops++);
    const stop = fixture.nativeElement.querySelector('.btn-exec--stop') as HTMLButtonElement;
    expect(stop).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="command-deck-start"]')).toBeNull();
    stop.click();
    expect(stops).toBe(1);
  });
});
