import { afterEach, describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { TokenSummaryBlockComponent } from './token-summary-block';

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
describe('TokenSummaryBlockComponent (smoke)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('compiles + instantiates without throwing', async () => {
    await TestBed.configureTestingModule({
      imports: [TokenSummaryBlockComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(TokenSummaryBlockComponent);
    fixture.componentRef.setInput('projectName', 'Demo');
    fixture.detectChanges();
    TestBed.inject(HttpTestingController)
      .expectOne('/api/runner/Demo/token-summary')
      .flush({
        project: 'Demo', orchestratorEntries: 0, orchestratorLlmCalls: 0,
        totalInputTokens: 0, totalOutputTokens: 0, totalCacheReadTokens: 0,
        totalCacheCreationTokens: 0, estimatedApiCostUsd: 0, allModelsPriced: false,
        unknownModelCount: 0, byModel: [], disclaimer: 'Estimate only.',
      });
    fixture.detectChanges();
    expect(fixture.componentInstance).toBeTruthy();
    fixture.destroy();
    TestBed.inject(HttpTestingController).verify();
  });

  it('shows a visible drift badge for active models absent from the catalog', async () => {
    await TestBed.configureTestingModule({
      imports: [TokenSummaryBlockComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(TokenSummaryBlockComponent);
    fixture.componentRef.setInput('projectName', 'Demo');
    fixture.detectChanges();

    TestBed.inject(HttpTestingController)
      .expectOne('/api/runner/Demo/token-summary')
      .flush({
        project: 'Demo',
        orchestratorEntries: 1,
        orchestratorLlmCalls: 1,
        totalInputTokens: 1_000,
        totalOutputTokens: 100,
        totalCacheReadTokens: 0,
        totalCacheCreationTokens: 0,
        estimatedApiCostUsd: 0,
        allModelsPriced: false,
        unknownModelCount: 1,
        byModel: [{
          model: 'future-model', modelId: 'future-model', cli: null, calls: 1, inputTokens: 1_000, outputTokens: 100,
          cacheReadTokens: 0, cacheCreationTokens: 0, estimatedApiCostUsd: 0,
          modelPriced: false, modelInCatalog: false,
        }],
        disclaimer: 'Estimate only.',
      });
    fixture.detectChanges();

    const badge = fixture.nativeElement.querySelector(
      '[data-testid="token-summary-pricing-drift"]',
    ) as HTMLElement | null;
    expect(badge?.textContent).toContain('1 model without price data');
    expect(fixture.nativeElement.querySelector('[data-testid="token-summary-cost"]')?.textContent)
      .toContain('Unknown');

    fixture.destroy();
    TestBed.inject(HttpTestingController).verify();
  });
});
